using System.Collections.Concurrent;
using System.Diagnostics;
using Foundation;
using MultiMon.Audio.Mac;
using MultiMon.Control.Shared;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Show;
using MultiMon.Core.Timing;
using MultiMon.Decode.Mac;
using MultiMon.Decode.Mac.Hap;
using MultiMon.Graphics.Mac;
using MultiMon.Platform.Mac;

namespace MultiMon.Control.Mac;

/// <summary>
/// The Mac performance orchestrator — the concrete <see cref="IPerformanceController"/> the control panel
/// drives, shape-for-shape the twin of <c>MultiMon.Control.PerformanceController</c>. It owns the WHOLE native
/// pipeline (the one Metal device + render loop + one persistent output window per monitor + the live decode
/// sources/passes + the audio engine), all built ONCE in the constructor and reused for the session.
/// Entering/leaving perform mode only shows/hides windows and starts/pauses the clock; applying a show re-binds
/// sources→outputs — the device, layers, windows, shaders and render loop are NEVER rebuilt (LESSON-ARCH-002).
///
/// <para>Threading / the V0087 rule: ONE long-lived worker thread with a FIFO command queue. Every public command
/// is posted to it and returns immediately, so the caller (the AppKit main thread in the app) never runs — or
/// blocks on — a native build or teardown (decode-thread joins, VideoToolbox session / Metal texture creation,
/// CoreAudio unit teardown). All per-show state is touched ONLY on the worker; FIFO keeps the ordering the UI
/// relies on. <see cref="StateChanged"/> / <see cref="CommandFailed"/> are raised ON the worker — handlers marshal.</para>
///
/// <para>Main-thread contract: the worker marshals AppKit window show/hide onto the main thread through the
/// bounded <see cref="MainThread.Invoke"/>, so the HOST MUST KEEP THE MAIN RUN LOOP PUMPING (the harness pumps
/// NSApplication events itself; an Avalonia host's NSApplication run loop drains the main dispatch queue). The
/// constructor and <see cref="Dispose"/> perform native work and wait on the render/worker threads, so they
/// refuse to run on the main thread: a host constructs and disposes the controller on a background thread
/// (Task.Run) and marshals the result back — the same rule the Windows App.OnExit teardown thread follows.</para>
/// </summary>
public sealed class PerformanceController : IPerformanceController, IPerformanceStatsSource
{
    private static readonly TimeSpan InFlightDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ILog _log;
    private readonly bool _forceSoftwareDecode;
    private readonly GraphicsDeviceProvider _provider;
    private readonly RenderLoop _loop;
    private readonly MasterClock _clock = new();
    private readonly OutputWindow[] _outputs;
    private readonly BlockingCollection<Action> _commands = new();
    private readonly Thread _worker;

    // Per-show state, rebuilt on ApplyShow. _outputs is fixed for the session; these are not.
    private readonly List<IMetalSource> _sources = new();
    private readonly List<FullscreenQuadPass> _passes = new();
    private readonly List<int> _activeOutputs = new(); // indices of outputs bound by the current show
    private readonly Dictionary<int, IMetalSource> _outputSources = new(); // output index → the source it samples
    private readonly DisplayModeService _displayModes;
    private readonly List<DisplayRefreshMatch> _refreshMatches = new();
    private readonly List<MasterClock> _freeRunClocks = new(); // Individual free-run: one clock per source
    private readonly Dictionary<int, double> _modeSwitchMs = new(); // output index → what its refresh match cost
    // The ONLY state GetStats reads: an immutable pair of (bound outputs, live sources) the worker republishes
    // whenever either changes. A caller on any thread reads the reference once and is consistent for the sample.
    private volatile StatsPublication _stats = StatsPublication.Empty;
    private AudioEngine? _audio;
    // Master mix settings live HERE, not in the per-show engine (LESSON-BUG-007): BuildAudio creates a fresh
    // AudioEngine on every ApplyShow and re-applies these to it. Worker-thread state like the rest.
    private double _masterVolume = 1.0;
    private bool _masterMuted;
    private volatile PerformState _state = PerformState.Idle;
    private volatile bool _disposed;

    public PerformState State => _state;
    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Switch each output's display to a refresh rate that is an integer multiple of its clip's frame rate
    /// for the length of the perform (25 fps on a 60 Hz panel is a 2-3 pull-down stutter; on 50/100 Hz it is clean),
    /// restored on ExitPerform. Read on the worker at EnterPerform; the panel toggles it between performs. Mac only —
    /// the Windows controller has no equivalent.</summary>
    public bool MatchDisplayRefresh { get; set; } = true;

    public event Action<PerformState>? StateChanged;
    public event Action<string>? CommandFailed;

    /// <summary>
    /// Builds the persistent pipeline for <paramref name="monitors"/> (one output window each) on the calling
    /// thread, which must NOT be the AppKit main thread (window creation round-trips to it). <paramref
    /// name="forceSoftwareDecode"/> is the harness's cross-GPU hook (the VideoToolbox software session).
    /// </summary>
    public PerformanceController(IReadOnlyList<MonitorInfo> monitors, ILog log, bool forceSoftwareDecode = false)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        if (NSThread.IsMain)
            throw new InvalidOperationException("PerformanceController must be constructed off the main thread (it builds the native pipeline and waits on the render thread).");

        _log = log;
        _forceSoftwareDecode = forceSoftwareDecode;
        Monitors = monitors;
        _displayModes = new DisplayModeService(log);

        _provider = new GraphicsDeviceProvider(log);
        _provider.Acquire();
        _loop = new RenderLoop(_provider, log) { Clock = _clock };
        _loop.Start();

        _outputs = new OutputWindow[monitors.Count];
        try
        {
            for (var i = 0; i < monitors.Count; i++)
                _outputs[i] = _loop.CreateOutputWindow($"MultiMon Output {i + 1}", monitors[i].Bounds);
        }
        catch
        {
            _loop.Stop(); // disposes the windows created so far; never leak a half-built pipeline
            _provider.Release();
            throw;
        }

        _worker = new Thread(WorkerProc) { Name = "MultiMon.Controller", IsBackground = true };
        _worker.Start();
    }

    // ── Public commands: post-and-return (the caller never waits on native work) ─────────────────

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

    /// <summary>An AVAsset track probe (not the Windows audible-signal probe: CoreAudio has no cheap silence
    /// scan). Off the UI thread, as the contract says. Best-effort; false on any failure.</summary>
    public bool FileHasAudio(string filePath) => AudioFileSource.HasAudioTrack(filePath);

    // Live mixer — master settings persist on the controller (survive engine rebuilds) and are pushed to the
    // current engine if a show with audio is playing. Track settings are per-show, so they stay on the engine.
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

    /// <summary>The worker loop: runs commands strictly FIFO, each under its own autorelease pool (LESSON-BUG-008:
    /// decoder sessions, textures and the AppKit round-trips return autoreleased objects on THIS thread). A failing
    /// command is logged + surfaced via <see cref="CommandFailed"/> and never kills the worker.</summary>
    private void WorkerProc()
    {
        foreach (var command in _commands.GetConsumingEnumerable())
        {
            using var pool = new NSAutoreleasePool();
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

    // ── Harness hooks (MultiMon.Stress.Mac only) ─────────────────────────────

    /// <summary>Posts a marker and waits until every command queued before it has run — the harness's
    /// per-cycle wedge detector. False if the worker did not get there within <paramref name="timeout"/>.</summary>
    internal bool WaitForQueue(TimeSpan timeout)
    {
        var done = new TaskCompletionSource();
        Post(() => done.SetResult());
        return done.Task.Wait(timeout);
    }

    /// <summary>Waits until every output bound by the current show has completed <paramref name="frames"/>
    /// more frames. Call only after <see cref="WaitForQueue"/> returned true (the bound set is worker state).</summary>
    internal bool WaitForPresentedFrames(int frames, TimeSpan timeout, out string stalled)
    {
        var targets = _activeOutputs.Select(i => _outputs[i].PresentCount + frames).ToArray();
        for (var n = 0; n < _activeOutputs.Count; n++)
        {
            var output = _outputs[_activeOutputs[n]];
            if (output.WaitForPresentCount(targets[n], timeout))
                continue;
            stalled = $"{output.Name} (phase={output.RenderPhase})";
            return false;
        }
        stalled = string.Empty;
        return true;
    }

    /// <summary>Waits until no output has a command buffer in flight, so a resource sample sees no drawable still
    /// counted. Same precondition as <see cref="WaitForPresentedFrames"/>.</summary>
    internal bool WaitForOutputsIdle(TimeSpan timeout) => _outputs.All(o => o.WaitForIdle(timeout));

    /// <summary>Per-source status of the current show, for the harness's advance/decoder-kind checks. Same
    /// precondition as <see cref="WaitForPresentedFrames"/>.</summary>
    internal IReadOnlyList<(string Id, string Kind, TimeSpan Pts, long Decoded, bool Faulted)> SourceStatus() =>
        _sources.Select(s => (s.Id, s.GetType().Name, s.CurrentPts, s.DecodedFrames, s.IsFaulted)).ToList();

    /// <summary>Audio gate counters of the current show's engine (null = no audio in the show) — the same fields the
    /// bind-once harness path reads off its own engine, so the controller path runs the SAME content-asserting gate
    /// (LESSON-TEST-005). Same precondition.</summary>
    internal (bool Active, double PeakDriftMs, long Underruns, bool Faulted, double RenderedSeconds, float PeakLeft, float PeakRight,
        IReadOnlyList<(string TrackId, string? DeviceUid)> OpenedDevices)? AudioStatus() =>
        _audio is null ? null : (_audio.Active, _audio.PeakDriftMs, _audio.Underruns, _audio.AnyFaulted,
                                 _audio.RenderedSeconds, _audio.PeakLeft, _audio.PeakRight, _audio.OpenedDevices);

    /// <summary>How many outputs of the current show run on their own clock (Individual free-run) — the harness's
    /// proof that <c>--free-run</c> actually took the free-run path. Same precondition.</summary>
    internal int FreeRunClockCount => _freeRunClocks.Count;

    /// <summary>What the last EnterPerform did to each active output's display refresh rate (empty when
    /// <see cref="MatchDisplayRefresh"/> is off or nothing is bound) — the harness's refresh gate. Same precondition.</summary>
    internal IReadOnlyList<DisplayRefreshMatch> RefreshMatches => _refreshMatches;

    /// <summary>Sum of every torn-down source's <see cref="IMetalSource.OutstandingAtDispose"/> — non-zero means a
    /// pooled texture was still held by GPU work when its source went away (the fence and the teardown order disagree).</summary>
    internal int TexturesOutstandingAtDispose { get; private set; }

    /// <summary>Tracked Metal objects still alive — read after <see cref="Dispose"/> by the app's exit path, which
    /// refuses to exit 0 while it is non-zero (the same measurement the harness gates on).</summary>
    public long TrackedLiveCount => _provider.Tracker.LiveCount;

    /// <summary>The tracker's own per-kind line, "live=N (textures=… buffers=… …)", as the harness prints it.</summary>
    public string TrackedResourceReport => _provider.Tracker.ToString();

    internal MetalResourceTracker Tracker => _provider.Tracker;
    internal ulong AllocatedBytes => _provider.CurrentAllocatedSize;
    internal RenderLoop Loop => _loop;
    internal OutputWindow[] Outputs => _outputs;
    internal GraphicsDeviceProvider Provider => _provider;

    // ── Live performance stats (read on the CALLER's thread) ─────────────────

    /// <summary>
    /// Samples the pipeline's live counters. It reads ONLY the immutable publication the worker last posted plus
    /// the Interlocked/volatile counters on the output windows and sources — so it never posts to the worker
    /// queue, never takes a lock, never blocks on native work and never runs on the render thread's behalf. Safe
    /// from the UI thread at any state, including Idle (which reports an empty snapshot).
    /// </summary>
    public PerformanceSnapshot GetStats()
    {
        var published = _stats;
        var outputs = new OutputStats[published.Bindings.Length];
        for (var n = 0; n < published.Bindings.Length; n++)
        {
            var b = published.Bindings[n];
            var window = b.Output;
            outputs[n] = new OutputStats(b.OutputIndex, window.Name, b.Source?.Id ?? string.Empty, window.Visible,
                b.PixelWidth, b.PixelHeight, b.RefreshHz, b.RefreshMatch,
                window.PresentCount, window.DrawableNullCount, window.InFlightTimeoutCount, window.LateFrameCount,
                window.StartupMs, window.StartupModeSwitchMs, window.StartupShowMs, window.RenderPhase);
        }
        var sources = new SourceStats[published.Sources.Length];
        for (var n = 0; n < published.Sources.Length; n++)
        {
            var source = published.Sources[n];
            sources[n] = new SourceStats(source.Id, source.DecodePath, source.FrameRate,
                source.CurrentPts.TotalSeconds, source.DecodedFrames, source.IsFaulted);
        }
        return new PerformanceSnapshot(_state, Stopwatch.GetTimestamp(), outputs, sources);
    }

    /// <summary>
    /// Worker thread: republish what <see cref="GetStats"/> may read — the bound outputs with their source, pixel
    /// size and CURRENT display refresh rate, and the live sources. Called whenever any of that changes (show
    /// built/torn down, refresh matched, refresh restored), so the rate is queried from CoreGraphics at most a
    /// handful of times per perform and never on a poll tick.
    /// </summary>
    private void PublishStats()
    {
        if (_activeOutputs.Count == 0)
        {
            _stats = StatsPublication.Empty;
            return;
        }

        var bindings = new StatsBinding[_activeOutputs.Count];
        for (var n = 0; n < _activeOutputs.Count; n++)
        {
            var i = _activeOutputs[n];
            _outputSources.TryGetValue(i, out var source);
            var bounds = Monitors[i].Bounds;
            var refreshHz = 0.0;
            if (uint.TryParse(Monitors[i].DeviceId, out var displayId) && _displayModes.QueryCurrent(displayId) is { } mode)
                refreshHz = mode.RefreshRate;
            var match = string.Empty;
            foreach (var m in _refreshMatches)
                if (m.Output == i)
                    match = m.Result.ToString();
            bindings[n] = new StatsBinding(i, _outputs[i], source, (int)bounds.Width, (int)bounds.Height, refreshHz, match);
        }
        _stats = new StatsPublication(bindings, _sources.ToArray());
    }

    /// <summary>What the worker publishes for <see cref="GetStats"/>: the bound outputs and the live sources as
    /// one immutable pair, so a reader can never see half of a rebind.</summary>
    private sealed record StatsPublication(StatsBinding[] Bindings, IMetalSource[] Sources)
    {
        public static StatsPublication Empty { get; } = new(Array.Empty<StatsBinding>(), Array.Empty<IMetalSource>());
    }

    /// <summary>One bound output as the stats reader sees it: the window whose counters it reads, the source it
    /// samples (null = test pattern) and the geometry/refresh cached at publish time.</summary>
    private sealed record StatsBinding(int OutputIndex, OutputWindow Output, IMetalSource? Source,
        int PixelWidth, int PixelHeight, double RefreshHz, string RefreshMatch);

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

        // Refresh-rate match BEFORE the windows appear: a mode switch blanks the display for about a second, and a
        // window shown first would flash through it. Same pixel and point size by construction, so the outputs'
        // bounds stay valid. Results are logged and recorded, never thrown — a display that will not switch performs
        // at its current rate.
        // Start-up timing (the user's "the second screen takes longer to start" question): one origin for this
        // EnterPerform, then what each output's own mode switch and Show cost. The render thread completes the
        // measurement when the output first draws decoded content and logs the line.
        var origin = Stopwatch.GetTimestamp();
        MatchOutputDisplayRefresh();

        // Show every bound output. A Show that fails partway (MainThread.Invoke timing out because the main run
        // loop stopped pumping) must not leave the earlier windows on screen with the state still Idle — ExitPerform's
        // Idle early-return would never hide them. Hide every active output (best effort, each on its own: the one
        // whose Show timed out may still appear when the main thread resumes, and the queue is FIFO) and rethrow →
        // CommandFailed; the state stays Idle and the clock never starts.
        try
        {
            foreach (var i in _activeOutputs)
            {
                var showStart = Stopwatch.GetTimestamp();
                _outputs[i].Show(Monitors[i].Bounds);
                _outputs[i].BeginStartupTiming(origin, _modeSwitchMs.GetValueOrDefault(i), Stopwatch.GetElapsedTime(showStart).TotalMilliseconds);
            }
        }
        catch
        {
            foreach (var i in _activeOutputs)
                TryHide(i, "rollback after a failed Show");
            _displayModes.RestoreAll();
            throw;
        }
        // Each performance starts its timeline at zero: ApplyShow rebuilt the sources (PTS 0) and the audio
        // engine (content position 0), so the clock MUST restart at 0 too — else audio "catches up" to the
        // banked clock time by dropping frames (the Windows garbled-start bug). Reset, then start.
        _clock.Reset();
        _clock.Start();                              // shared clock (synced modes + free-run fallback)
        foreach (var c in _freeRunClocks) c.Start(); // Individual free-run: independent timelines
        PublishStats();                              // the matched refresh rates are now the current ones
        SetState(PerformState.Performing);
    }

    private void ExitPerformCore()
    {
        if (_state == PerformState.Idle)
            return;

        // Pause the clock (decode self-paces to a halt on the full timeline) then hide. Content stays bound so a
        // re-enter is an instant Show+Start. No native teardown here — that only happens on ApplyShow / Dispose.
        _clock.Stop();
        foreach (var c in _freeRunClocks) c.Stop();
        foreach (var s in _sources)
            if (s.IsFaulted)
                _log.Error("Control", $"Source '{s.Id}' faulted during the show (see Decode lines above); it is rebuilt on the next Perform.");
        foreach (var i in _activeOutputs)
            TryHide(i, "ExitPerform"); // one failed Hide must not skip the remaining outputs
        _displayModes.RestoreAll();   // after the windows are gone, so they never flash through the switch back
        PublishStats();               // re-cache the restored refresh rates
        SetState(PerformState.Idle);
    }

    /// <summary>Hides one output, logging instead of throwing (a main-thread timeout must not leave the other
    /// outputs of a multi-window change on screen).</summary>
    private void TryHide(int index, string phase)
    {
        try
        {
            _outputs[index].Hide();
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"{_outputs[index].Name}: Hide on the main thread failed during {phase} (main run loop not pumping?): {ex.Message}");
        }
    }

    /// <summary>For each active output, the bound source's frame rate decides the display's target rate (Span/Split:
    /// the one shared source on every output; Individual: each output's own). Worker thread.</summary>
    private void MatchOutputDisplayRefresh()
    {
        _refreshMatches.Clear();
        _modeSwitchMs.Clear();
        if (!MatchDisplayRefresh)
            return;
        foreach (var i in _activeOutputs)
        {
            if (!_outputSources.TryGetValue(i, out var source))
                continue; // the test pattern has no frame rate
            if (!uint.TryParse(Monitors[i].DeviceId, out var displayId))
            {
                _log.Info("Control", $"{_outputs[i].Name}: monitor '{Monitors[i].DeviceId}' is not a CGDirectDisplayID; refresh rate left alone.");
                continue;
            }
            var matchStart = Stopwatch.GetTimestamp();
            var result = _displayModes.Match(displayId, source.FrameRate);
            _modeSwitchMs[i] = Stopwatch.GetElapsedTime(matchStart).TotalMilliseconds;
            _refreshMatches.Add(new DisplayRefreshMatch(i, displayId, source.FrameRate, result));
            _log.Info("Control", $"{_outputs[i].Name}: display {displayId}: {result.Before.PixelWidth}x{result.Before.PixelHeight} {result} for {source.FrameRate:0.###} fps clip '{source.Id}'");
        }
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
            // Stop the clock(s): the render loop keeps presenting, but frame selection holds at the frozen
            // media time, so the picture stays put and the windows remain visible.
            _clock.Stop();
            foreach (var c in _freeRunClocks) c.Stop();
            SetState(PerformState.Paused);
        }
        var snapshot = string.Join(" ", _activeOutputs.Select(i => $"{_outputs[i].Name}={_outputs[i].PresentCount}"));
        _log.Info("Control", $"TogglePause -> {(_state == PerformState.Paused ? "PAUSED" : "RESUMED")} (mode-clocks={_freeRunClocks.Count} free-run + 1 shared) presents:[{snapshot}]");
    }

    // ── Show build / teardown (worker thread) ────────────────────────────────

    private void BuildShow(ShowDefinition show)
    {
        // WHICH source feeds WHICH output through WHICH UV slice on WHICH clock is pure geometry, decided by
        // ShowPlanner in Core. All that is left here is turning that plan into native objects.
        var plan = ShowPlanner.Plan(show, Monitors);
        foreach (var warning in plan.Warnings)
            _log.Error("Control", warning);

        if (show.Sources.Count == 0)
        {
            // A show with no clip is the animated test pattern on every output (an unbound pass draws it) —
            // the Mac harness's no-video gate; the Windows twin has no pattern pass to bind.
            var pattern = new FullscreenQuadPass(_provider);
            _passes.Add(pattern);
            for (var i = 0; i < _outputs.Length; i++)
                BindOutput(i, pattern, UvRect.Full);
        }

        for (var i = 0; i < plan.Sources.Count; i++)
        {
            var built = BuildSource(plan.Sources[i]);
            if (built is null)
                continue; // clip unopenable on any path — its outputs stay black (logged), never crash
            var (pass, source) = built.Value;

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
                BindOutput(binding.OutputIndex, pass, binding.Uv, clock, source.FrameRate);
                _outputSources[binding.OutputIndex] = source;
            }
        }

        BuildAudio(show);

        foreach (var s in _sources)
            s.Start();

        PublishStats();
    }

    /// <summary>
    /// Builds one (source + pass) for a planned source through the symmetric decode ladder (LESSON-TEST-004: the
    /// gate and the app open clips the SAME way): a .mov — or any clip a HAP mode asked for — is probed as HAP
    /// first and falls through to VideoToolbox when it is not a HAP movie; the ladder itself gates HAP on the
    /// clip's format. HAP is an ENHANCEMENT: a HAP-mode clip that lands on VideoToolbox plays, with an error
    /// line. If nothing can open the clip, SKIP the source (that output stays black) — NEVER crash.
    /// Returns the bound pass and its source, or null when nothing could open the clip.
    /// </summary>
    private (FullscreenQuadPass Pass, IMetalSource Source)? BuildSource(PlannedSource planned)
    {
        IMetalSource? source = null;
        var pass = new FullscreenQuadPass(_provider);
        try
        {
            source = SourceLadder.Open(planned.FilePath, _provider, _log, requireHap: false, _forceSoftwareDecode, planned.SourceId);
            if (planned.PreferHap && source is not HapSource)
                _log.Error("Control", $"'{planned.FilePath}' was asked for as HAP but is not a HAP movie; decoding via VideoToolbox.");
            pass.BindSource(source.Frames, source.Width, source.Height, source.TextureFormat, source.UseYCoCg);
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"No decode path could open '{planned.FilePath}' ({ex.Message}); that output stays black.");
            source?.Dispose();
            pass.Dispose();
            return null;
        }
        _sources.Add(source);
        _passes.Add(pass);
        return (pass, source);
    }

    /// <summary>Binds an output to a pass + UV slice, optionally with its own free-run clock (null = shared).
    /// <paramref name="sourceFps"/> is the bound clip's frame rate (0 for the test pattern) — the output's
    /// late-frame counter measures against one frame period of it.</summary>
    private void BindOutput(int index, FullscreenQuadPass pass, UvRect uv, MasterClock? clock = null, double sourceFps = 0)
    {
        _outputs[index].SetContent(pass, uv, clock, sourceFps);
        _activeOutputs.Add(index);
    }

    private void BuildAudio(ShowDefinition show)
    {
        if (show.AudioTracks.Count == 0)
            return;
        _audio = new AudioEngine(_clock, _log, show.AudioTracks);
        // Carry the master mix across the rebuild: the new engine defaults to unity/unmuted.
        _audio.SetMasterVolume(_masterVolume);
        _audio.SetMasterMuted(_masterMuted);
        _audio.Start();
    }

    /// <summary>Unbinds outputs (marshalled → the render thread drops the old passes; their in-flight reads drain),
    /// then stops + disposes the previous show's sources/passes/audio on the worker in producer-before-consumer
    /// order. Never touches the device, windows or layers.</summary>
    private void TeardownShow()
    {
        ExitPerformCore();

        // The unbinds marshal to the render thread. If it has died (a fatal render fault) Invoke logs and returns
        // false — the sources/passes below must still be stopped and disposed, or Dispose would leak them.
        foreach (var i in _activeOutputs)
        {
            _outputs[i].SetContent(null);
            if (!_outputs[i].WaitForIdle(InFlightDrainTimeout))
                _log.Error("Control", $"{_outputs[i].Name}: command buffers still in flight {InFlightDrainTimeout.TotalSeconds:0}s after unbind.");
        }
        _activeOutputs.Clear();
        _outputSources.Clear();

        foreach (var s in _sources) s.Stop();   // joins each decode thread
        _audio?.Stop();                          // decode producers, then the AUHAL units
        foreach (var p in _passes) p.Dispose();  // persistent source textures
        foreach (var s in _sources)
        {
            s.Dispose();                         // timeline frames, then the decoder's texture pool
            TexturesOutstandingAtDispose += s.OutstandingAtDispose;
        }
        _audio?.Dispose();

        _passes.Clear();
        _sources.Clear();
        _freeRunClocks.Clear();
        _audio = null;
        PublishStats();
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
    /// Ordered teardown ON the worker (the V0087 rule — never on the main thread): tear down the show
    /// (stop+dispose sources/passes/audio) → stop+join the render loop (windows and layers die there, closed on
    /// the main thread) → release the device. Queued after any pending command, then the worker exits. BLOCKS
    /// until the worker has finished, so call it from a background thread that may wait — never the main thread,
    /// which must stay free to pump the window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (NSThread.IsMain)
            throw new InvalidOperationException("PerformanceController.Dispose must not be called on the main thread (teardown is off the UI thread).");
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
            _displayModes.Dispose(); // idempotent restore — TeardownShow's ExitPerform already did it if performing
            _loop.Stop();          // joins the render thread; all output windows/layers disposed there
            _provider.Release();
            _provider.Dispose();
        });
        _commands.CompleteAdding();
        _worker.Join();
        _commands.Dispose();
    }
}

/// <summary>One output's display-refresh decision at EnterPerform: which display, the clip fps it was matched to,
/// and what <see cref="DisplayModeService"/> did.</summary>
public readonly record struct DisplayRefreshMatch(int Output, uint DisplayId, double Fps, DisplayMatchResult Result);
