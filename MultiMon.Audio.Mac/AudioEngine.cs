using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;

namespace MultiMon.Audio.Mac;

/// <summary>
/// Routes <see cref="AudioTrack"/>s to CoreAudio output devices, clocked to the shared
/// <see cref="MasterClock"/> (REBUILD_ARCHITECTURE §2.2) — the Mac twin of <c>MultiMon.Audio.AudioEngine</c>.
/// Each track becomes its own pipeline of <see cref="AudioFileSource"/> (decode thread → PCM) →
/// <see cref="AudioRing"/> → <see cref="CoreAudioOutput"/> (one AUHAL unit PER TRACK).
///
/// <para><b>Per-track outputs, not per-device:</b> tracks that target the same device each open their own AUHAL
/// unit on it and CoreAudio's HAL mixes them — there is no in-process mixer. That is deliberate: each track keeps
/// its native rate/channel count (AUHAL converts per unit), and every output drift-corrects independently against
/// the SAME MasterClock with the same threshold and one-shot policy, so the tracks stay mutually aligned without a
/// shared mix buffer.</para>
///
/// <para><b>Master lives above (LESSON-BUG-007):</b> the engine is rebuilt per show, so master volume/mute is NOT
/// owned here — it is owned by the session-long controller, which re-applies it to each freshly built engine via
/// <see cref="SetMasterVolume"/>/<see cref="SetMasterMuted"/> before <see cref="Start"/>. The engine only holds
/// the value it was given.</para>
///
/// <para><b>Never crash (success metric):</b> if a device can't be opened or a track can't be decoded, that
/// pipeline is logged and skipped — the rest of the app keeps running. Teardown is ordered and off the main
/// thread: stop every producer (decode), then stop every consumer (AUHAL unit + its device watch), then release.</para>
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    /// <summary>Live mixer state for one track (the AudioTrack model holds only the initial values).</summary>
    private sealed class TrackState
    {
        public required string Id { get; init; }
        public required CoreAudioOutput Output { get; init; }
        public double Volume { get; set; }
        public bool Muted { get; set; }
        public bool Solo { get; set; }
    }

    private readonly MasterClock _clock;
    private readonly ILog _log;
    private readonly IReadOnlyList<AudioTrack> _tracks;
    private readonly List<(AudioFileSource source, CoreAudioOutput output, AutoResetEvent space)> _pipelines = new();
    private readonly List<TrackState> _trackStates = new();
    private readonly object _mixGate = new();
    private double _masterVolume = 1.0;
    private bool _masterMuted;
    private bool _started;
    private bool _disposed;

    public AudioEngine(MasterClock clock, ILog log, IEnumerable<AudioTrack> tracks)
    {
        _clock = clock;
        _log = log;
        _tracks = tracks.ToList();
    }

    /// <summary>True when at least one track pipeline is live (audio is actually playing).</summary>
    public bool Active => _pipelines.Count > 0;

    /// <summary>Enumerate the CoreAudio output devices for device selection. Static so the control UI can populate
    /// its device list before any engine exists.</summary>
    public static IReadOnlyList<AudioOutputDevice> EnumerateDevices(ILog log) => AudioDeviceEnumerator.Enumerate(log);

    /// <summary>Available output devices (delegates to <see cref="EnumerateDevices"/>).</summary>
    public IReadOnlyList<AudioOutputDevice> GetDevices() => AudioDeviceEnumerator.Enumerate(_log);

    /// <summary>Peak |audio − MasterClock| across all outputs, in ms (the A/V alignment gate).</summary>
    public double PeakDriftMs => _pipelines.Count == 0 ? 0 : _pipelines.Max(p => p.output.PeakDriftMs);

    /// <summary>Total post-prime ring underruns across all outputs (should stay 0).</summary>
    public long Underruns => _pipelines.Sum(p => p.output.Underruns);

    /// <summary>True when any track's decode loop ended on an error — the harness fails the run on it.</summary>
    public bool AnyFaulted => _pipelines.Any(p => p.source.IsFaulted);

    /// <summary>Build and start every track pipeline. Idempotent; skips (logs) any track that fails.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        foreach (var track in _tracks)
            BuildPipeline(track);

        lock (_mixGate)
            RecomputeGains(); // apply the master/solo state across all tracks once they're all built

        _log.Info("Audio", _pipelines.Count > 0
            ? $"AudioEngine started: {_pipelines.Count} track(s), one AUHAL output unit each (device per track: requested UID or default)."
            : "AudioEngine started: no audio outputs active (no tracks, or no device available).");
    }

    private void BuildPipeline(AudioTrack track)
    {
        var gain = (float)(track.IsMuted ? 0.0 : Math.Clamp(track.Volume, 0.0, 1.0));

        // Open the decoder first: it discovers the file's native float format, which the output unit's input
        // scope is then set to (AUHAL converts to the device's format).
        AudioFileSource source;
        try
        {
            source = new AudioFileSource(track.SourceFilePath, _log, track.Name);
        }
        catch (Exception ex)
        {
            _log.Error("Audio", $"track '{track.Name}' could not be decoded; skipping: {ex.Message}");
            return;
        }

        // ~0.5 s of bounded lookahead at the decoded format; caps held PCM, paces the decoder.
        var ring = new AudioRing(source.SampleRate / 2, source.Channels);
        var space = new AutoResetEvent(false); // consumer→producer "ring drained" handshake (no busy-spin)
        var output = new CoreAudioOutput(_log, _clock, gain, source.SampleRate, source.Channels, ring, space, track.OutputDeviceId);
        if (!output.Open()) // device unavailable → log+skip (already logged inside Open)
        {
            output.Dispose();
            source.Dispose();
            space.Dispose();
            return;
        }

        // The unit is live (rendering silence until the clock runs); now start the producer.
        source.Start(ring, space);
        _pipelines.Add((source, output, space));

        output.SetPan((float)Math.Clamp(track.Pan, -1.0, 1.0));
        lock (_mixGate)
            _trackStates.Add(new TrackState { Id = track.Id, Output = output, Volume = track.Volume, Muted = track.IsMuted, Solo = track.IsSolo });
    }

    // ── Live mixer controls — safe from any thread; push effective gains to the outputs ──

    public void SetMasterVolume(double volume) { lock (_mixGate) { _masterVolume = Math.Clamp(volume, 0, 1); RecomputeGains(); } }
    public void SetMasterMuted(bool muted) { lock (_mixGate) { _masterMuted = muted; RecomputeGains(); } }

    public void SetTrackVolume(string id, double volume) => UpdateTrack(id, t => t.Volume = Math.Clamp(volume, 0, 1));
    public void SetTrackMuted(string id, bool muted) => UpdateTrack(id, t => t.Muted = muted);
    public void SetTrackSolo(string id, bool solo) => UpdateTrack(id, t => t.Solo = solo);

    public void SetTrackPan(string id, double pan)
    {
        lock (_mixGate)
            _trackStates.FirstOrDefault(t => t.Id == id)?.Output.SetPan((float)Math.Clamp(pan, -1.0, 1.0));
    }

    private void UpdateTrack(string id, Action<TrackState> change)
    {
        lock (_mixGate)
        {
            var state = _trackStates.FirstOrDefault(t => t.Id == id);
            if (state is null) return;
            change(state);
            RecomputeGains();
        }
    }

    /// <summary>Recompute each track's effective gain = master × track-volume × mute × solo, and push it to the
    /// output. Solo wins: if ANY track is soloed, the non-soloed tracks are silenced. Call under _mixGate.</summary>
    private void RecomputeGains()
    {
        var master = _masterMuted ? 0.0 : _masterVolume;
        var anySolo = _trackStates.Any(t => t.Solo);
        foreach (var t in _trackStates)
        {
            var audible = !t.Muted && (!anySolo || t.Solo);
            t.Output.SetGain((float)(audible ? master * t.Volume : 0.0));
        }
    }

    /// <summary>Ordered stop: every producer (decode) first, then every consumer (AUHAL). Off the main thread.</summary>
    public void Stop()
    {
        foreach (var (source, _, _) in _pipelines)
            source.Stop();
        foreach (var (_, output, _) in _pipelines)
            output.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        foreach (var (source, output, space) in _pipelines)
        {
            output.Dispose();
            source.Dispose();
            space.Dispose();
        }
        _pipelines.Clear();
        lock (_mixGate)
            _trackStates.Clear();
    }
}
