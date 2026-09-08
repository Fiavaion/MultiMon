using AudioToolbox;
using AudioUnit;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Timing;

namespace MultiMon.Audio.Mac;

/// <summary>
/// One AUHAL output unit (<c>kAudioUnitSubType_HALOutput</c>) on a CoreAudio render device and the render
/// callback that feeds it — the Mac twin of <c>MultiMon.Audio.WasapiOutput</c>. The <see cref="AudioEngine"/>
/// creates one per TRACK (several may share a device — CoreAudio's HAL mixes them, exactly as shared-mode
/// WASAPI does).
///
/// <para><b>AUHAL, not AVAudioEngine:</b> AUHAL is the only one of the two that can be pointed at a specific
/// output device (<c>kAudioOutputUnitProperty_CurrentDevice</c>), which <see cref="MultiMon.Core.Models.AudioTrack.OutputDeviceId"/>
/// requires, and its render callback is the direct analogue of the WASAPI fill — so the drift policy below is a
/// line-for-line port rather than a re-derivation. AVAudioEngine would add a graph and a node we do not use.</para>
///
/// <para><b>Format:</b> the unit's INPUT scope is set to the DECODER's native interleaved-float format and AUHAL
/// converts to the device's stream format on its way out — the same contract as WASAPI's
/// <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM</c>, so the decoder never resamples and this class never assumes the
/// device's rate or bit depth.</para>
///
/// <para><b>One owner (LESSON-ARCH-001):</b> the unit is created, rebuilt and released under
/// <see cref="_unitGate"/> by the engine thread and the device-watch thread only. The render callback runs on
/// CoreAudio's realtime IO thread and touches NOTHING but the ring, the volatile mixer values and the clock — no
/// locks, no allocation, no ObjC calls (so it needs no autorelease pool). <see cref="AudioUnit.AudioUnit.Stop"/>
/// does not return until the IO thread has left the callback, so teardown after Stop can never race it.</para>
///
/// <para><b>Clocked to the MasterClock (ADR 0002 D1):</b> the device drains at its own crystal, so A/V alignment
/// is by drift correction: the callback tracks how much real clip content it has rendered
/// (<c>_contentFrames</c>) → media time, compares to <see cref="MasterClock.CurrentMediaTime"/>, and DROPS ring
/// frames when behind / INSERTS silence when ahead, one-shot and proportional so a correction never overshoots.
/// It never reseeks the decoder. While the clock is paused it renders silence and does not advance content time,
/// so resume stays aligned.</para>
/// </summary>
public sealed class CoreAudioOutput : IDisposable
{
    private const double DriftCorrectThresholdSec = 0.012; // start correcting before drift is audible (~12ms)
    private static readonly TimeSpan DeviceWatchInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxRebuildAttempts = 3;

    private readonly ILog _log;
    private readonly MasterClock _clock;
    private readonly AudioRing _ring;
    private readonly AutoResetEvent _spaceAvailable; // pulsed after a Read frees ring space → wakes the decoder
    private readonly string? _targetDeviceUid;       // requested device UID (null/empty = system default)
    private readonly RenderDelegate _renderCallback; // held for the unit's lifetime: the native trampoline points here

    private float _gain;  // live: mixer volume×mute×solo×master, read per fill (Volatile)
    private float _pan;   // live: stereo balance -1..+1, applied for 2-channel output
    private float[] _discard = Array.Empty<float>(); // reusable scratch for dropped (drift) frames

    // Unit + device state, owned by the engine / watch threads under _unitGate.
    private readonly object _unitGate = new();
    private AudioComponent? _component;
    private AudioUnit.AudioUnit? _unit;
    private uint _deviceId;

    private readonly Thread _watch;
    private readonly ManualResetEventSlim _watchWake = new(false);
    private volatile bool _stop;
    private volatile bool _disposed;
    private volatile bool _primed;      // false until the ring has filled once against a running clock
    private volatile bool _interleavedWarned;

    private long _contentFrames;        // real clip frames rendered (excludes silence) → media position
    private long _underruns;            // fills that starved post-prime (Interlocked)
    private long _peakDriftMicros;      // max |audio - clock| seen post-prime (Interlocked)

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Fills that starved (ring empty while running) after priming — should stay 0.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Peak |audio − MasterClock| observed while running, in milliseconds (A/V drift gate).</summary>
    public double PeakDriftMs => Interlocked.Read(ref _peakDriftMicros) / 1000.0;

    /// <summary>Live mixer controls (any thread): the render callback reads them per fill via Volatile.</summary>
    public void SetGain(float gain) => Volatile.Write(ref _gain, Math.Clamp(gain, 0f, 1f));
    public void SetPan(float pan) => Volatile.Write(ref _pan, Math.Clamp(pan, -1f, 1f));

    public CoreAudioOutput(ILog log, MasterClock clock, float gain, int sampleRate, int channels, AudioRing ring,
        AutoResetEvent spaceAvailable, string? deviceUid = null)
    {
        _log = log;
        _clock = clock;
        _gain = Math.Clamp(gain, 0f, 1f);
        _ring = ring;
        _spaceAvailable = spaceAvailable;
        _targetDeviceUid = deviceUid;
        SampleRate = sampleRate;
        Channels = channels;
        _renderCallback = Render;
        _watch = new Thread(WatchProc) { Name = "MultiMon.Audio.DeviceWatch", IsBackground = true };
    }

    /// <summary>
    /// Resolve the device, build + start the output unit, and start the device watch. Returns false on failure
    /// (logged) — the engine then skips this track's audio rather than crashing (the success metric).
    /// The unit runs immediately, rendering silence until the MasterClock starts.
    /// </summary>
    public bool Open()
    {
        try
        {
            lock (_unitGate)
                Activate();
        }
        catch (Exception ex)
        {
            _log.Error("Audio", $"CoreAudio output open failed; audio disabled for this track: {ex.Message}");
            lock (_unitGate)
                ReleaseUnit();
            return false;
        }
        _watch.Start();
        return true;
    }

    /// <summary>
    /// Build the AUHAL unit on the resolved device and start it. Called at open AND on the device-invalidated
    /// rebuild path. Caller holds <see cref="_unitGate"/>.
    /// </summary>
    private void Activate()
    {
        _deviceId = ResolveDevice();

        _component = AudioComponent.FindComponent(AudioTypeOutput.HAL)
            ?? throw new NotSupportedException("the HAL output audio component is unavailable.");
        _unit = _component.CreateAudioUnit()
            ?? throw new NotSupportedException("the HAL output audio unit could not be instantiated.");

        // Device first, then the input-scope format: AUHAL validates the format against the bound device.
        Check(_unit.SetCurrentDevice(_deviceId, AudioUnitScopeType.Global, 0), "SetCurrentDevice");

        // The decoder's native interleaved float format; AUHAL converts to the device's stream format.
        var format = new AudioStreamBasicDescription(AudioFormatType.LinearPCM)
        {
            SampleRate = SampleRate,
            FormatFlags = AudioStreamBasicDescription.AudioFormatFlagsNativeFloat,
            BitsPerChannel = 32,
            ChannelsPerFrame = Channels,
            FramesPerPacket = 1,
            BytesPerFrame = Channels * sizeof(float),
            BytesPerPacket = Channels * sizeof(float),
        };
        Check(_unit.SetFormat(format, AudioUnitScopeType.Input, 0), "SetFormat");
        Check(_unit.SetRenderCallback(_renderCallback, AudioUnitScopeType.Input, 0), "SetRenderCallback");
        Check(_unit.Initialize(), "Initialize");

        var maxFrames = _unit.GetMaximumFramesPerSlice(AudioUnitScopeType.Global, 0);
        _discard = new float[Math.Max(1, maxFrames) * Channels];
        _primed = false;

        Check(_unit.Start(), "Start");
        _log.Info("Audio", $"CoreAudio output '{AudioDeviceEnumerator.DescribeDevice(_deviceId)}' " +
                           $"({(_targetDeviceUid is { Length: > 0 } ? _targetDeviceUid : "default")}): feeding {SampleRate}Hz {Channels}ch float " +
                           $"(AUHAL-converted), maxFrames={maxFrames}, gain={_gain:0.00}.");
    }

    /// <summary>Resolve the requested device UID, falling back to the system default when it is unset or gone.</summary>
    private uint ResolveDevice()
    {
        if (!string.IsNullOrEmpty(_targetDeviceUid))
        {
            var id = AudioDeviceEnumerator.FindByUid(_targetDeviceUid);
            if (id != 0)
                return id;
            _log.Error("Audio", $"requested audio device '{_targetDeviceUid}' unavailable; using the default output device.");
        }

        var fallback = AudioDeviceEnumerator.DefaultOutputDeviceId();
        if (fallback == 0)
            throw new InvalidOperationException("no CoreAudio output device is available.");
        return fallback;
    }

    private static void Check(AudioUnitStatus status, string call)
    {
        if (status != AudioUnitStatus.NoError)
            throw new InvalidOperationException($"AudioUnit.{call} returned {status}.");
    }

    // ── Device-invalidated recovery (the Windows D-005 sequencing) ─────────────────────────────────────────

    /// <summary>
    /// Watches for the two invalidations that end a stream: the bound device dying
    /// (<c>kAudioDevicePropertyDeviceIsAlive</c>) and — when the track follows the system default — the default
    /// output moving to another device. Either one rebuilds the unit HERE, off the IO thread and off the engine
    /// thread, so the realtime callback never touches unit lifetime. Polled rather than driven by an
    /// <c>AudioObjectAddPropertyListener</c> block: the poll cannot deliver a native callback into managed code
    /// after teardown, which is the failure mode this app exists to avoid.
    /// </summary>
    private void WatchProc()
    {
        try
        {
            while (!_watchWake.Wait(DeviceWatchInterval))
            {
                if (_stop)
                    return;

                uint current;
                lock (_unitGate)
                    current = _deviceId;
                if (current == 0)
                    continue;

                var followsDefault = string.IsNullOrEmpty(_targetDeviceUid);
                var defaultId = followsDefault ? AudioDeviceEnumerator.DefaultOutputDeviceId() : current;
                var moved = followsDefault && defaultId != 0 && defaultId != current;
                if (!moved && AudioDeviceEnumerator.IsAlive(current))
                    continue;

                _log.Error("Audio", moved
                    ? $"CoreAudio default output moved to '{AudioDeviceEnumerator.DescribeDevice(defaultId)}'; rebuilding the output."
                    : "CoreAudio output device is no longer alive; rebuilding the output.");
                if (!TryRebuild())
                {
                    if (!_stop)
                        _log.Error("Audio", "CoreAudio output rebuild failed after retries; stopping this output (audio ends, app continues).");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Audio", $"CoreAudio device watch ended on exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Release the dead unit, re-resolve the device (requested UID, else default), rebuild and start. Bounded
    /// retries with a short settle wait so a flapping device cannot spin. The content position is realigned to
    /// the clock by the prime re-baseline in <see cref="Render"/> once the rebuilt unit re-primes, so the
    /// decoder is never seeked. Watch thread only.
    /// </summary>
    private bool TryRebuild()
    {
        for (var attempt = 1; attempt <= MaxRebuildAttempts && !_stop; attempt++)
        {
            try
            {
                lock (_unitGate)
                    ReleaseUnit();
                // Let the device settle (re-enumeration after a switch or unplug). Waiting on the stop signal
                // rather than sleeping keeps Stop() prompt.
                if (_watchWake.Wait(TimeSpan.FromMilliseconds(120 * attempt)) || _stop)
                    return false;
                lock (_unitGate)
                    Activate();
                _log.Info("Audio", $"CoreAudio output rebuilt (attempt {attempt}); audio resumed.");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Audio", $"CoreAudio rebuild attempt {attempt}/{MaxRebuildAttempts} failed: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>Stop and release the unit (and its component wrapper). Caller holds <see cref="_unitGate"/>.
    /// After <c>Stop</c> returns, CoreAudio's IO thread has left the render callback.</summary>
    private void ReleaseUnit()
    {
        if (_unit is not null)
        {
            try { _unit.Stop(); } catch { /* stopping a lost device is expected to fail */ }
            try { _unit.Uninitialize(); } catch { /* ditto */ }
            _unit.Dispose();
            _unit = null;
        }
        _component?.Dispose();
        _component = null;
    }

    // ── Render callback: CoreAudio's realtime IO thread ────────────────────────────────────────────────────

    /// <summary>
    /// Fill one IO cycle. Runs on CoreAudio's realtime thread: no locks, no allocation, no ObjC. The buffer is
    /// always fully written (content, silence, or both) — a short write would play stale audio.
    /// </summary>
    private AudioUnitStatus Render(AudioUnitRenderActionFlags flags, AudioTimeStamp timestamp, uint bus,
        uint frameCount, AudioBuffers data)
    {
        if (data.Count != 1)
        {
            // Our ASBD is interleaved, so AUHAL must ask for exactly one buffer. Anything else means the format
            // was not honoured: render silence rather than write a wrong layout.
            for (var i = 0; i < data.Count; i++)
                Zero(data[i]);
            if (!_interleavedWarned)
            {
                _interleavedWarned = true;
                _log.Error("Audio", $"CoreAudio asked for {data.Count} buffers on an interleaved stream; rendering silence.");
            }
            return AudioUnitStatus.NoError;
        }

        var buffer = data[0];
        var floats = (int)frameCount * Channels;
        if (buffer.Data == IntPtr.Zero || buffer.DataByteSize < floats * sizeof(float))
            return AudioUnitStatus.NoError;

        unsafe
        {
            var destination = new Span<float>((void*)buffer.Data, floats);

            // Paused (between perform cycles): render silence, hold content position so resume stays aligned.
            if (!_clock.IsRunning)
            {
                destination.Clear();
                return AudioUnitStatus.NoError;
            }

            // Prime: don't begin consuming (and don't count underruns) until the decoder has filled the ring, so
            // the first fills after start don't register a spurious startup starvation.
            if (!_primed)
            {
                if (_ring.AvailableToRead < floats)
                {
                    destination.Clear();
                    return AudioUnitStatus.NoError;
                }
                // Re-baseline the content position to the clock at the instant we start consuming. The clock has
                // been running since EnterPerform (and through the ring prime), so the first content sample is
                // anchored to it: drift starts at ~0 and we never "catch up" by dropping a chunk of audio at t=0.
                // Also covers re-priming after a device rebuild mid-performance (LESSON-BUG-007's sibling: a
                // freshly built output must not assume the clock is at zero).
                _contentFrames = (long)(_clock.CurrentMediaTime.TotalSeconds * SampleRate);
                _primed = true;
            }

            // Drift = how far the audio's content position is ahead(+)/behind(-) the MasterClock.
            var audioSec = _contentFrames / (double)SampleRate;
            var driftSec = audioSec - _clock.CurrentMediaTime.TotalSeconds;
            var absMicros = (long)(Math.Abs(driftSec) * 1_000_000);
            if (absMicros > Interlocked.Read(ref _peakDriftMicros))
                Interlocked.Exchange(ref _peakDriftMicros, absMicros);

            // Proportional, ONE-SHOT correction — close exactly the drift, never overshoot (a full-buffer
            // insert/drop oscillates and chops). Both branches are capped to one fill so a correction is bounded.
            var silenceFrames = 0;
            if (driftSec > DriftCorrectThresholdSec)
            {
                // Audio AHEAD: prepend exactly the surplus as silence; the clock catches up by that much.
                silenceFrames = Math.Min((int)(driftSec * SampleRate), (int)frameCount);
            }
            else if (driftSec < -DriftCorrectThresholdSec)
            {
                // Audio BEHIND: drop exactly the deficit so content catches up, then fill normally.
                var dropFloats = Math.Min((int)(-driftSec * SampleRate) * Channels, _discard.Length);
                var dropped = dropFloats > 0 ? _ring.Read(_discard.AsSpan(0, dropFloats)) : 0;
                if (dropped > 0)
                {
                    _contentFrames += dropped / Channels;
                    _spaceAvailable.Set();
                }
            }

            // Fill: optional silence prefix (the surplus), then content for the remainder, gain-scaled.
            var contentStart = silenceFrames * Channels;
            if (contentStart > 0)
                destination[..contentStart].Clear();

            var content = destination[contentStart..];
            var read = _ring.Read(content);
            if (read > 0)
                ApplyGainAndPan(content[..read]);
            if (read < content.Length)
            {
                content[read..].Clear();
                Interlocked.Increment(ref _underruns); // genuine starvation of the content portion
            }

            _contentFrames += read / Channels;  // inserted silence is NOT counted → the clock catches up
            if (read > 0)
                _spaceAvailable.Set();           // freed ring space → wake the decoder
        }
        return AudioUnitStatus.NoError;
    }

    private static void Zero(AudioBuffer buffer)
    {
        if (buffer.Data != IntPtr.Zero && buffer.DataByteSize > 0)
            unsafe { new Span<byte>((void*)buffer.Data, buffer.DataByteSize).Clear(); }
    }

    /// <summary>Apply the live mixer gain (and stereo pan for 2-channel output) to a span of interleaved float
    /// samples. Reads the volatile controls once so a mid-fill change can't tear within the buffer.</summary>
    private void ApplyGainAndPan(Span<float> samples)
    {
        var gain = Volatile.Read(ref _gain);
        var pan = Volatile.Read(ref _pan);

        if (Channels == 2 && pan != 0f)
        {
            // Constant-ish balance: attenuate the opposite channel as pan moves off-centre.
            var left = gain * (pan > 0f ? 1f - pan : 1f);
            var right = gain * (pan < 0f ? 1f + pan : 1f);
            for (var i = 0; i + 1 < samples.Length; i += 2)
            {
                samples[i] *= left;
                samples[i + 1] *= right;
            }
            return;
        }

        if (gain != 1f)
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= gain;
    }

    /// <summary>Stop the device watch and the unit, off the UI/main thread. Idempotent.</summary>
    public void Stop()
    {
        if (_disposed)
            return;
        _stop = true;
        _watchWake.Set();
        if (_watch.IsAlive)
            _watch.Join();
        lock (_unitGate)
            ReleaseUnit();
    }

    /// <summary>Idempotent: a second Dispose must not signal the already-disposed wake event.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
        _watchWake.Dispose();
    }
}
