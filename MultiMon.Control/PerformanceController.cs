using System.Collections.Concurrent;
using MultiMon.Audio;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Show;
using MultiMon.Core.Timing;
using MultiMon.Decode.Hap;
using MultiMon.Decode.MediaFoundation;
using MultiMon.Graphics;
using MultiMon.Platform;

namespace MultiMon.Control;

/// <summary>
/// The performance orchestrator (ADR 0003 D3) — the concrete <see cref="IPerformanceController"/> the
/// WPF control panel drives. It owns the WHOLE native pipeline (one D3D11 device + render loop + one
/// output window per monitor + the live decode sources/passes + the audio engine + device-removed
/// recovery), all built ONCE and reused for the session. Entering/leaving perform mode only shows/hides
/// windows and starts/pauses the clock; applying a show re-binds sources→outputs — the device,
/// swapchains, shaders, and render loop are NEVER rebuilt (LESSON-ARCH-002).
///
/// <para>Threading / the V0087 rule: the controller owns ONE long-lived worker thread with a FIFO command
/// queue. Every public command (ApplyShow / EnterPerform / ExitPerform / TogglePause / mixer / the Dispose
/// teardown) is posted to it and returns immediately, so the UI thread never runs — or blocks on — a
/// native build or teardown (SetContent(null) joins the render thread, source Stop joins decode threads,
/// IMFSourceReader/D3D/WASAPI disposal, MF source construction). All per-show state below is touched ONLY
/// on the worker; FIFO keeps the ordering the UI relies on (an EnterPerform posted after an ApplyShow runs
/// after it completes). <see cref="StateChanged"/> / <see cref="CommandFailed"/> are raised ON the worker —
/// handlers marshal. The binding logic mirrors the path the stress harness gates (--controller) per mode.</para>
/// </summary>
public sealed class PerformanceController : IPerformanceController
{
    private readonly ILog _log;
    private readonly GraphicsDeviceProvider _provider;
    private readonly RenderLoop _loop;
    private readonly MasterClock _clock = new();
    private readonly OutputWindow[] _outputs;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _worker;
    private MfDeviceManager? _mf;

    // Per-show state, rebuilt on ApplyShow. _outputs is fixed for the session; these are not.
    private readonly List<ISource> _sources = new();
    private readonly List<FullscreenQuadPass> _passes = new();
    private readonly List<int> _activeOutputs = new(); // indices of outputs bound by the current show
    private readonly List<MasterClock> _freeRunClocks = new(); // Individual free-run: one clock per source
    private AudioEngine? _audio;
    // Master mix settings live HERE, not in the per-show engine: BuildAudio creates a fresh AudioEngine on
    // every ApplyShow, so a master set on the old engine would be lost. The controller is the stable owner
    // across rebuilds; BuildAudio re-applies these to each new engine. Worker-thread state like the rest.
    private double _masterVolume = 1.0;
    private bool _masterMuted;
    private volatile PerformState _state = PerformState.Idle;
    private volatile bool _disposed;

    public PerformState State => _state;
    public IReadOnlyList<MonitorInfo> Monitors { get; }

    public event Action<PerformState>? StateChanged;
    public event Action<string>? CommandFailed;

    /// <summary>
    /// Builds the persistent pipeline for <paramref name="monitors"/> (one output window each). The device
    /// is created on the primary adapter; cross-adapter outputs are DWM-composited (ADR 0002 D4).
    /// <paramref name="enableDebugLayer"/> is the harness hook (live-object counting); the app leaves it off.
    /// </summary>
    public PerformanceController(IReadOnlyList<MonitorInfo> monitors, ILog log, bool enableDebugLayer = false)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        _log = log;
        Monitors = monitors;

        _provider = new GraphicsDeviceProvider(log, enableDebugLayer);
        _provider.Acquire();
        // Vendor/name follow the adapter the device actually runs on (WMI can name the wrong GPU on hybrid rigs).
        GpuCapabilityService.ApplyDeviceAdapter(_provider.DeviceAdapterName ?? "Unknown", _provider.DeviceAdapterVendorId, log);
        AdapterMap.LogTopology(_provider, log);

        _loop = new RenderLoop(_provider, log) { Clock = _clock };
        _loop.Start();

        _outputs = new OutputWindow[monitors.Count];
        for (var i = 0; i < monitors.Count; i++)
            _outputs[i] = _loop.CreateOutputWindow($"MultiMon Output {i + 1}", monitors[i].Bounds);

        _worker = new Thread(WorkerProc) { Name = "MultiMon.Controller", IsBackground = true };
        _worker.Start();
    }

    // ── Public commands: post-and-return (the UI thread never waits on native work) ──────────────

    public void ApplyShow(ShowDefinition show)
    {
        ArgumentNullException.ThrowIfNull(show);
        Post(() =>
        {
            TeardownShow();      // unbind + dispose any previous show (never touches the device)
            try
            {
                BuildShow(show); // build sources/passes + bind outputs for the new show
            }
            catch
            {
                TeardownShow();  // never leave a half-built show bound; the queued EnterPerform then no-ops
                throw;
            }
        });
    }

    public void EnterPerform() => Post(EnterPerformCore);
    public void ExitPerform() => Post(ExitPerformCore);
    public void TogglePause() => Post(TogglePauseCore);

    public IReadOnlyList<AudioOutputDevice> GetAudioDevices() => AudioEngine.EnumerateDevices(_log);

    /// <summary>Probe whether a file has AUDIBLE audio — not just a stream, but actual signal (a silent
    /// placeholder track, common in camera/NLE exports, does not count). Keeps the decode type behind the
    /// firewall — the VM never references MultiMon.Audio. A full MF decode probe: callers run it off the
    /// UI thread (the view-model uses a Task). Best-effort; false on any failure.</summary>
    public bool FileHasAudio(string filePath) => MfAudioSource.HasAudibleAudio(filePath);

    // Live mixer — master settings persist on the controller (survive engine rebuilds) and are pushed to the
    // current engine if a show with audio is playing. Track settings are per-show, so they stay on the engine.
    // Posted like everything else: _audio is created/disposed on the worker, so only the worker touches it.
    public void SetMasterVolume(double volume) => Post(() => { _masterVolume = volume; _audio?.SetMasterVolume(volume); });
    public void SetMasterMuted(bool muted) => Post(() => { _masterMuted = muted; _audio?.SetMasterMuted(muted); });
    public void SetTrackVolume(string trackId, double volume) => Post(() => _audio?.SetTrackVolume(trackId, volume));
    public void SetTrackMuted(string trackId, bool muted) => Post(() => _audio?.SetTrackMuted(trackId, muted));
    public void SetTrackSolo(string trackId, bool solo) => Post(() => _audio?.SetTrackSolo(trackId, solo));
    public void SetTrackPan(string trackId, double pan) => Post(() => _audio?.SetTrackPan(trackId, pan));

    /// <summary>Queues <paramref name="command"/> on the worker. Throws only if already disposed.</summary>
    private void Post(Action command)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PerformanceController));
        _commands.Add(command);
    }

    /// <summary>The worker loop: runs commands strictly FIFO. A failing command is logged + surfaced via
    /// <see cref="CommandFailed"/> and never kills the worker (the next command still runs).</summary>
    private void WorkerProc()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                _log.Error("Control", $"Controller command failed: {ex}");
                CommandFailed?.Invoke(ex.Message);
            }
        }
    }

    // ── Harness hooks (MultiMon.Stress only) ─────────────────────────────────

    /// <summary>Posts a marker and waits until every command queued before it has run — the harness's
    /// per-cycle wedge detector. False if the worker did not get there within <paramref name="timeout"/>.</summary>
    internal bool WaitForQueue(TimeSpan timeout)
    {
        var done = new TaskCompletionSource();
        Post(() => done.SetResult());
        return done.Task.Wait(timeout);
    }

    /// <summary>Waits until every output bound by the current show has presented <paramref name="frames"/>
    /// more frames. Call only after <see cref="WaitForQueue"/> returned true (the bound set is worker state).</summary>
    internal bool WaitForPresentedFrames(int frames, TimeSpan timeout, out string stalled)
    {
        foreach (var i in _activeOutputs)
        {
            if (_outputs[i].WaitForPresentedFrames(frames, timeout))
                continue;
            stalled = $"{_outputs[i].Name} (deviceLost={_outputs[i].DeviceLost})";
            return false;
        }
        stalled = string.Empty;
        return true;
    }

    internal bool DebugLayerActive => _provider.DebugLayerActive;
    internal int GetLiveObjectCount() => _provider.GetLiveObjectCount();

    // ── Worker-thread implementations ────────────────────────────────────────

    private void EnterPerformCore()
    {
        if (_state != PerformState.Idle)
            return;
        if (_activeOutputs.Count == 0)
        {
            CommandFailed?.Invoke("No source could be opened (see the log).");
            return;
        }

        foreach (var i in _activeOutputs)
            _outputs[i].Show(Monitors[i].Bounds);
        // Each performance starts its timeline at zero. ApplyShow rebuilt the sources (PTS 0) and the
        // audio engine (content position 0) just before this, so the clock MUST also restart at 0 — else a
        // re-perform resumes the clock's banked time while audio restarts at 0, and the audio render thread
        // "catches up" by dropping frames for ~that many seconds (the garbled-start bug). Reset, then start.
        _clock.Reset();
        _clock.Start();                              // shared clock (synced modes + free-run fallback)
        foreach (var c in _freeRunClocks) c.Start(); // Individual free-run: independent timelines
        SetState(PerformState.Performing);
    }

    private void ExitPerformCore()
    {
        if (_state == PerformState.Idle)
            return;

        // Pause the clock (decode self-blocks while paused) then hide. Content stays bound so a re-enter
        // is an instant Show+Start. No native teardown here — that only happens on ApplyShow / Dispose.
        _clock.Stop();
        foreach (var c in _freeRunClocks) c.Stop();
        foreach (var s in _sources)
            if (s.IsFaulted)
                _log.Error("Control", $"Source '{s.Id}' faulted during the show (see Decode lines above); it is rebuilt on the next Perform.");
        try
        {
            foreach (var i in _activeOutputs)
                _outputs[i].Hide();
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"Hide on the render thread failed (render loop dead?): {ex.Message}");
        }
        SetState(PerformState.Idle);
    }

    private void TogglePauseCore()
    {
        if (_state == PerformState.Idle)
            return;

        if (_state == PerformState.Paused)
        {
            _clock.Start();
            foreach (var c in _freeRunClocks) c.Start();
            SetState(PerformState.Performing);
        }
        else
        {
            // Stop the clock(s): the render loop keeps presenting, but frame selection holds at the
            // frozen media time, so the picture stays put and the windows remain visible.
            _clock.Stop();
            foreach (var c in _freeRunClocks) c.Stop();
            SetState(PerformState.Paused);
        }
        // Pause-freeze diagnostic: mark the transition + a present snapshot so the render-loop present-rate
        // log can be read relative to it (which output, if any, stops advancing after a pause).
        var snapshot = string.Join(" ", _activeOutputs.Select(i => $"{_outputs[i].Name}={_outputs[i].PresentCount}"));
        _log.Info("Control", $"TogglePause -> {(_state == PerformState.Paused ? "PAUSED" : "RESUMED")} (mode-clocks={_freeRunClocks.Count} free-run + 1 shared) presents:[{snapshot}]");
    }

    // ── Show build / teardown (worker thread) ────────────────────────────────

    private void BuildShow(ShowDefinition show)
    {
        // WHICH source feeds WHICH output through WHICH UV slice on WHICH clock is pure geometry, decided
        // by ShowPlanner in Core (and unit-tested there without a device). All that is left here is turning
        // that plan into native objects.
        var plan = ShowPlanner.Plan(show, Monitors);
        foreach (var warning in plan.Warnings)
            _log.Error("Control", warning);

        for (var i = 0; i < plan.Sources.Count; i++)
        {
            var pass = BuildSource(plan.Sources[i]);
            if (pass is null)
                continue; // clip unopenable on any path — its outputs stay black (logged), never crash

            foreach (var binding in plan.Bindings)
            {
                if (binding.SourceIndex != i)
                    continue;
                MasterClock? clock = null;
                if (binding.Clock == ShowClock.FreeRun)
                {
                    clock = new MasterClock();  // own timeline; started/stopped with the show
                    _freeRunClocks.Add(clock);
                }
                BindOutput(binding.OutputIndex, pass, binding.Uv, clock);
            }
        }

        BuildAudio(show);
        BuildRecovery();

        foreach (var s in _sources)
            s.Start();
    }

    /// <summary>
    /// Builds one (source + pass) for a planned source, choosing the decode path and surviving an
    /// unsupported clip. HAP is an ENHANCEMENT (hap-playback.md): if it's gated off on this GPU (blacklist
    /// or the clip's BCn format isn't supported) or the clip can't open, fall back to Media Foundation; if
    /// MF can't open it either, SKIP the source (that output stays black) — NEVER crash (the success
    /// metric). HAP only ever ships in a QuickTime container, so a .mov in a non-HAP mode
    /// (Individual/Span/Split) is probed on the HAP path FIRST — a plain H.264 .mov fails the HAP demux
    /// instantly and proceeds to MF, while a HAP .mov plays wherever the GPU allows it instead of going
    /// black behind an MF "codec not found". Returns the bound pass, or null when nothing could open the clip.
    /// </summary>
    private FullscreenQuadPass? BuildSource(PlannedSource planned)
    {
        var pass = new FullscreenQuadPass(_provider.Device);
        var probeHap = planned.PreferHap || planned.FilePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);
        ISource? source = probeHap ? TryBuildHapSource(planned, pass, expected: planned.PreferHap) : null;
        source ??= TryBuildMfSource(planned, pass);
        if (source is null)
        {
            pass.Dispose();
            _log.Error("Control", $"No decode path could open '{planned.FilePath}'; that output stays black.");
            return null;
        }
        _sources.Add(source);
        _passes.Add(pass);
        return pass;
    }

    /// <summary>Try the HAP path, GATED on GPU capability (G1). Returns the bound source, or null to fall
    /// back to Media Foundation: HAP gated off (blacklist), the clip's BCn format unsupported on this GPU,
    /// or the clip won't open. Never throws — HAP is never a hard requirement.</summary>
    private HapSource? TryBuildHapSource(PlannedSource planned, FullscreenQuadPass pass, bool expected)
    {
        if (!GpuCapabilityService.SupportsHap)
        {
            _log.Info("Control", $"HAP gated off on this GPU ({GpuCapabilityService.DetectedGpuName}); " +
                                 $"decoding '{planned.FilePath}' via Media Foundation.");
            return null;
        }
        HapSource? hap = null;
        try
        {
            hap = new HapSource(planned.FilePath, _log, planned.SourceId);
            if (!_provider.SupportsTextureFormat(hap.TextureFormat))
            {
                _log.Info("Control", $"GPU does not support {hap.TextureFormat} for HAP '{planned.FilePath}'; " +
                                     "falling back to Media Foundation.");
                hap.Dispose();
                return null;
            }
            pass.BindSource(hap.Frames, hap.Width, hap.Height, hap.TextureFormat, hap.UseYCoCg);
            return hap;
        }
        catch (Exception ex)
        {
            // A non-HAP .mov probed in a non-HAP mode is expected to land here; only a mode that asked for
            // HAP gets an error line.
            var line = $"HAP open failed for '{planned.FilePath}' ({ex.Message}); falling back to Media Foundation.";
            if (expected) _log.Error("Control", line); else _log.Info("Control", line);
            hap?.Dispose();
            return null;
        }
    }

    /// <summary>Try the Media Foundation path. Returns the bound source, or null if MF can't open the clip
    /// (e.g. a HAP-codec .mov that fell back here but has no MF-decodable track) — the output is then skipped.</summary>
    private MediaFoundationSource? TryBuildMfSource(PlannedSource planned, FullscreenQuadPass pass)
    {
        MediaFoundationSource? src = null;
        try
        {
            _mf ??= new MfDeviceManager(_provider.Device, _log,
                preferSoftwareDecode: GpuCapabilityService.PreferSoftwareDecode || !_provider.MultithreadProtected);
            src = new MediaFoundationSource(_provider.Device, _mf, planned.FilePath, _log, planned.SourceId);
            pass.BindSource(src.Frames, src.Width, src.Height);
            return src;
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"Media Foundation could not open '{planned.FilePath}' ({ex.Message}).");
            src?.Dispose();
            return null;
        }
    }

    /// <summary>Binds an output to a pass + UV slice, optionally with its own free-run clock (null = shared).</summary>
    private void BindOutput(int index, FullscreenQuadPass pass, UvRect uv, MasterClock? clock = null)
    {
        _outputs[index].SetContent(pass, uv, clock);
        _activeOutputs.Add(index);
    }

    private void BuildAudio(ShowDefinition show)
    {
        if (show.AudioTracks.Count == 0)
            return;
        _audio = new AudioEngine(_clock, _log, show.AudioTracks);
        // Carry the master mix across the rebuild: the new engine defaults to unity/unmuted, so without this
        // any master the user set on a prior engine would silently reset to full on every ApplyShow.
        _audio.SetMasterVolume(_masterVolume);
        _audio.SetMasterMuted(_masterMuted);
        _audio.Start();
    }

    private void BuildRecovery()
    {
        var mfSources = _sources.OfType<MediaFoundationSource>().ToArray();
        IDecodeRecovery? decode = mfSources.Length > 0 && _mf is not null
            ? new MfDecodeRecovery(_mf, mfSources, _log)
            : null;
        var handler = new DeviceRemovedHandler(_provider, _passes.ToArray(), decode, _clock, _log);
        // Assign on the render thread so it never reads a half-set reference mid-iteration.
        _loop.Invoke(() => _loop.RecoveryHandler = handler);
    }

    /// <summary>Unbinds outputs (marshalled → render thread drops the old passes), then stops + disposes
    /// the previous show's sources/passes/audio on the worker. Never touches the device/swapchains.</summary>
    private void TeardownShow()
    {
        ExitPerformCore();

        // The unbinds marshal to the render thread. If that thread has died (a fatal render fault) the
        // Invoke throws — log it and carry on: the sources/passes below must still be stopped and disposed,
        // or Dispose would leak them and never release the device.
        try
        {
            _loop.Invoke(() => _loop.RecoveryHandler = null);
            foreach (var i in _activeOutputs)
                _outputs[i].SetContent(null); // blocks until the render thread drops the pass reference
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"Unbind on the render thread failed (render loop dead?): {ex.Message}");
        }
        _activeOutputs.Clear();

        foreach (var s in _sources) s.Stop();
        _audio?.Stop();
        foreach (var p in _passes) p.Dispose();
        foreach (var s in _sources) s.Dispose();
        _audio?.Dispose();

        _passes.Clear();
        _sources.Clear();
        _freeRunClocks.Clear();
        _audio = null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetState(PerformState state)
    {
        if (_state == state)
            return;
        _state = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>
    /// Ordered teardown ON the worker (the V0087 rule — never on the UI thread): tear down the show
    /// (stop+dispose sources/passes/audio) → stop+join the render loop (windows + swapchains die on the
    /// render thread) → dispose the MF device manager → release the device. Queued after any pending
    /// command, then the worker exits. BLOCKS until the worker has finished, so call it from a thread that
    /// may wait (App.OnExit uses a time-guarded teardown thread). Tolerates a dead render loop: the unbind
    /// failures are logged and Stop/Release still run.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _commands.Add(() =>
        {
            try
            {
                TeardownShow();
            }
            catch (Exception ex)
            {
                // A show-teardown fault must not stop the device from being released below.
                _log.Error("Control", $"Show teardown failed during Dispose: {ex}");
            }
            _loop.Stop();          // joins the render thread; all output windows/swapchains disposed there
            _mf?.Dispose();
            _provider.Release();
        });
        _commands.CompleteAdding();
        _worker.Join();
        _commands.Dispose();
    }
}
