using MultiMon.Core.Sync;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// The decode→render handoff for time-correct playback (ADR 0002 D2), replacing M2's latest-only
/// TripleBuffer. The decode thread <see cref="Publish"/>es frames in ascending PTS into a bounded
/// buffer; the render thread, per present, asks for the frame matching the MasterClock time via
/// <see cref="SelectInto"/> (which uses <see cref="FrameSelector"/>) and prunes the frames it has
/// passed. Bounded depth caps held decoder-pool textures.
///
/// Pacing: <see cref="Publish"/> BLOCKS while the buffer is full, so the decode thread self-paces to
/// real time — when the render thread advances the clock and prunes a passed frame, a slot frees and
/// decode resumes. This is real-time clocking, not a Sleep masking a bug (LESSON-BUG-001 is about
/// masking crashes). Teardown releases any blocked producer via <see cref="SignalStop"/>.
///
/// Single-producer (decode) / single-consumer (render). A short lock guards the small ordered list;
/// the GPU copy runs OUTSIDE the lock on the selected frame, which the decode thread never disposes
/// (it only adds), so there is no cross-thread race on a live texture.
/// </summary>
public sealed class FrameTimeline : IDisposable
{
    private readonly object _gate = new();
    private readonly List<DecodedFrame> _frames = new(); // ascending PTS
    private readonly List<TimeSpan> _ptsScratch = new(); // reused under _gate — no per-present allocation
    private readonly int _capacity;
    private bool _stopped;
    private TimeSpan? _lastPublishedPts;

    public FrameTimeline(int capacity = 8) => _capacity = Math.Max(2, capacity);

    /// <summary>Decode thread: append a frame (ascending PTS). Blocks while full so decode tracks real time.
    /// The ascending contract is ENFORCED: a PTS not greater than the previous published one is refused — the
    /// frame is disposed and the producer gets an exception (its loop faults loudly) — because
    /// <see cref="FrameSelector"/>'s binary search over a non-ascending list silently selects stale frames
    /// (the VideoToolbox decode-order stutter, 2026-09-13).</summary>
    public void Publish(DecodedFrame frame)
    {
        lock (_gate)
        {
            if (_lastPublishedPts is { } last && frame.Pts <= last)
            {
                frame.Dispose();
                throw new InvalidOperationException(
                    $"FrameTimeline: first inversion — published PTS {frame.Pts.TotalSeconds:0.000}s is not greater than the previous {last.TotalSeconds:0.000}s; frames must be published in presentation order.");
            }
            while (_frames.Count >= _capacity && !_stopped)
                Monitor.Wait(_gate);

            if (_stopped)
            {
                frame.Dispose();
                return;
            }
            _frames.Add(frame);
            _lastPublishedPts = frame.Pts;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Render thread: copy the frame whose PTS best matches <paramref name="targetTime"/> into the
    /// caller's texture via <paramref name="copy"/>, and dispose the frames it has passed. Returns
    /// false when no frame is buffered yet. The selected frame stays buffered so a later present with
    /// no new frame can re-copy it.
    /// </summary>
    public bool SelectInto(TimeSpan targetTime, Action<DecodedFrame> copy)
    {
        DecodedFrame selected;
        List<DecodedFrame>? passed = null;
        lock (_gate)
        {
            if (_frames.Count == 0)
                return false;

            _ptsScratch.Clear();
            foreach (var frame in _frames)
                _ptsScratch.Add(frame.Pts);

            var index = FrameSelector.Select(_ptsScratch, targetTime);
            selected = _frames[index];
            if (index > 0)
            {
                passed = _frames.GetRange(0, index);
                _frames.RemoveRange(0, index);
                Monitor.PulseAll(_gate); // freed slots → wake a blocked producer
            }
        }

        // Outside the lock: the decode thread only appends and never disposes existing frames, so the
        // selected frame stays alive while we copy it.
        copy(selected);
        if (passed is not null)
            foreach (var frame in passed)
                frame.Dispose();
        return true;
    }

    /// <summary>
    /// Device-removed recovery: dispose every buffered frame (they hold the dying device's textures) and
    /// re-arm the timeline so the rebuilt decoder can publish again. PRECONDITION (as for <see cref="Dispose"/>):
    /// the producer thread is stopped/joined and the render thread is not in <see cref="SelectInto"/>.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            foreach (var frame in _frames)
                frame.Dispose();
            _frames.Clear();
            _lastPublishedPts = null;
            _stopped = false; // re-arm: a new decode thread may Publish again after rebind
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Release a producer blocked in <see cref="Publish"/> at teardown (before joining its thread).</summary>
    public void SignalStop()
    {
        lock (_gate)
        {
            _stopped = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Frees every buffered frame. PRECONDITION: the render thread must no longer be in
    /// <see cref="SelectInto"/> — i.e. the render loop is already stopped/joined — or the copy of a
    /// selected frame could race this disposal into a GPU use-after-free. The teardown order enforces
    /// this: stop decode → stop+join render loop → dispose pass → dispose source (which calls this).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _stopped = true;
            foreach (var frame in _frames)
                frame.Dispose();
            _frames.Clear();
            Monitor.PulseAll(_gate);
        }
    }
}
