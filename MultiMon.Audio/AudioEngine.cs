using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;

namespace MultiMon.Audio;

/// <summary>
/// Routes <see cref="AudioTrack"/>s to WASAPI render endpoints, clocked to the shared
/// <see cref="MasterClock"/> (REBUILD_ARCHITECTURE §2.2). Each track becomes a pipeline of
/// <see cref="MfAudioSource"/> (decode thread → PCM) → <see cref="AudioRing"/> →
/// <see cref="WasapiOutput"/> (one render thread per device). Built ONCE at <see cref="Start"/> and
/// reused for the session; the MasterClock pausing/resuming per perform cycle gates content vs silence
/// inside each <see cref="WasapiOutput"/> — the engine itself does not churn per cycle.
///
/// <para>M6 routes every track to the DEFAULT render endpoint (one device, one render thread). The
/// per-device grouping that multi-device routing needs lands with the M7 control layer.</para>
///
/// <para><b>Never crash (success metric):</b> if the endpoint can't be opened or a track can't be
/// decoded, that pipeline is logged and skipped — the rest of the app keeps running. Teardown is
/// ordered and off the UI thread: stop every producer (decode), then stop+join every consumer (render),
/// then release.</para>
/// </summary>
public sealed class AudioEngine : IAudioEngine
{
    /// <summary>Live mixer state for one track (the AudioTrack model holds only the initial values).</summary>
    private sealed class TrackState
    {
        public required string Id { get; init; }
        public required WasapiOutput Output { get; init; }
        public double Volume { get; set; }
        public bool Muted { get; set; }
        public bool Solo { get; set; }
    }

    private readonly MasterClock _clock;
    private readonly ILog _log;
    private readonly IReadOnlyList<AudioTrack> _tracks;
    private readonly List<(MfAudioSource source, WasapiOutput output, AutoResetEvent space)> _pipelines = new();
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

    /// <summary>Enumerate the active WASAPI render endpoints for device selection (D-005). Static so the
    /// control UI can populate its device list before any engine exists.</summary>
    public static IReadOnlyList<AudioOutputDevice> EnumerateDevices(ILog log) => WasapiOutput.EnumerateDevices(log);

    /// <summary>Available render endpoints (delegates to <see cref="EnumerateDevices"/>).</summary>
    public IReadOnlyList<AudioOutputDevice> GetDevices() => WasapiOutput.EnumerateDevices(_log);

    /// <summary>Peak |audio − MasterClock| across all outputs, in ms (the A/V alignment gate).</summary>
    public double PeakDriftMs => _pipelines.Count == 0 ? 0 : _pipelines.Max(p => p.output.PeakDriftMs);

    /// <summary>Total post-prime ring underruns across all outputs (should stay 0).</summary>
    public long Underruns => _pipelines.Sum(p => p.output.Underruns);

    /// <summary>Build and start every track pipeline. Idempotent; skips (logs) any track that fails.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;

        foreach (var track in _tracks)
            BuildPipeline(track);

        lock (_mixGate)
            RecomputeGains(); // apply initial master/solo across all tracks once they're all built

        _log.Info("Audio", _pipelines.Count > 0
            ? $"AudioEngine started: {_pipelines.Count} track(s) routed to the default endpoint."
            : "AudioEngine started: no audio outputs active (no tracks, or no endpoint available).");
    }

    private void BuildPipeline(AudioTrack track)
    {
        var gain = (float)(track.IsMuted ? 0.0 : Math.Clamp(track.Volume, 0.0, 1.0));

        // Open the decoder first: it discovers the file's native float format, which the endpoint then
        // initialises against (WASAPI auto-converts to the device mix).
        MfAudioSource source;
        try
        {
            source = new MfAudioSource(track.SourceFilePath, _log, track.Name);
        }
        catch (Exception ex)
        {
            _log.Error("Audio", $"track '{track.Name}' could not be decoded; skipping: {ex.Message}");
            return;
        }

        // ~0.5 s of bounded lookahead at the decoded format; caps held PCM, paces the decoder.
        var ring = new AudioRing(source.SampleRate / 2, source.Channels);
        var space = new AutoResetEvent(false); // consumer→producer "ring drained" handshake (no busy-spin)
        var output = new WasapiOutput(_log, _clock, gain, source.SampleRate, source.Channels, ring, space, track.OutputDeviceId);
        if (!output.Open()) // device unavailable → log+skip (already logged inside Open)
        {
            output.Dispose();
            source.Dispose();
            space.Dispose();
            return;
        }

        // Output's render thread is live (rendering silence until the clock runs); now start the producer.
        source.Start(ring, space);
        _pipelines.Add((source, output, space));

        output.SetPan((float)Math.Clamp(track.Pan, -1.0, 1.0));
        lock (_mixGate)
            _trackStates.Add(new TrackState { Id = track.Id, Output = output, Volume = track.Volume, Muted = track.IsMuted, Solo = track.IsSolo });
    }

    // ── Live mixer controls (M7 Stage B) — safe from any thread; push effective gains to the outputs ──

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

    /// <summary>Recompute each track's effective gain = master × track-volume × mute × solo, and push it to
    /// the WASAPI output. Solo wins: if ANY track is soloed, the non-soloed tracks are silenced. Call under _mixGate.</summary>
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

    /// <summary>Ordered stop: every producer (decode) first, then every consumer (render). Off the UI thread.</summary>
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
