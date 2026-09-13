using System.Diagnostics;
using Foundation;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Sync;
using MultiMon.Core.Timing;
using MultiMon.Audio.Mac;
using MultiMon.Control.Mac;
using MultiMon.Control.Shared;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Show;
using MultiMon.Decode.Mac;
using MultiMon.Decode.Mac.Hap;
using MultiMon.Graphics.Mac;
using MultiMon.Platform.Mac;

namespace MultiMon.Stress.Mac;

/// <summary><paramref name="Hap"/> refuses the ladder's VideoToolbox fallthrough (a HAP gate must run HAP);
/// <paramref name="Video2"/> feeds the second output in Individual mode; <paramref name="ForceSwDecode"/> forces the
/// VideoToolbox software session (the Windows <c>--force-sw-decode</c>); <paramref name="Audio"/> adds a real audio
/// track per cycle, from <paramref name="AudioFile"/> or the video's own audio stream. <paramref name="Mode"/> is the
/// show mode (Span = ONE source, each output samples its UvLayout slice; Individual/Hap = one source per output, full
/// frame; Split = one source through the auto-grid cells). <paramref name="Controller"/> drives the real
/// <see cref="PerformanceController"/> instead of the bind-once loop. <paramref name="AudioDevice"/> routes the
/// audio track to that CoreAudio device UID (<c>--list-audio</c>; an unknown UID fails the run);
/// <paramref name="AudioGain"/> / <paramref name="AudioPan"/> / <paramref name="AudioMaster"/> drive the engine's
/// live track-volume / track-pan / master-volume setters so the mixer DSP is exercised and metered.
/// <paramref name="FreeRun"/> gives each Individual-mode source its OWN clock (the Windows <c>--free-run</c>).
/// <paramref name="MatchRefresh"/> (default on; <c>--no-match-refresh</c>) lets the controller switch each display to a
/// multiple of its clip's fps per perform, and gates that it did and that the mode came back.</summary>
public sealed record StressOptions(int Cycles, int Windows, bool Fullscreen, int SoakSeconds, bool SourceCheck,
    bool Hap, string? Video, string? Video2, bool ForceSwDecode, ShowMode Mode, bool Audio, string? AudioFile, bool Controller,
    string? AudioDevice, double? AudioGain, double? AudioPan, double? AudioMaster, bool FreeRun, bool MatchRefresh);

/// <summary>
/// The Mac twin of <c>MultiMon.Stress.StressHarness</c>'s bind-once loop: build the persistent Metal pipeline
/// ONCE, then churn EnterPerform (show + bind) → hold until frames complete (the wedge detector) → ExitPerform
/// (unbind + hide) WITHOUT destroying anything. Cycles alternate the test pattern with a bound SOURCE (a frame
/// published through <see cref="FrameTimeline"/> each sourced cycle, sampled through a quadrant UV rect) so the
/// decode→render handoff churns under the same growth gate. With <c>--video=CLIP</c> every cycle is the
/// app's real per-perform path instead: open the clip through the real decode ladder (<see cref="SourceLadder"/>:
/// HAP for a HAP .mov, else VideoToolbox; <c>--hap</c> refuses the fallthrough) + start the source(s) on the
/// persistent device → bind → hold while the clip advances (asserted from each source's PTS against the
/// MasterClock) → unbind → stop (join, off the main thread) → dispose → sample. <c>--mode=span</c> shares ONE
/// source across the outputs through their UvLayout slices; <c>--mode=individual</c> gives each output its own
/// source (<c>--video2</c> for the second). <c>--audio [--audio-file=PATH]</c> adds the real audio path on top of
/// either: a fresh <see cref="AudioEngine"/> (AVAssetReader decode → ring → AUHAL) is built, started, stopped and
/// disposed EVERY cycle — the app's per-perform rebuild — and the run fails unless every cycle ends with zero
/// post-prime underruns and a peak |audio − MasterClock| under <see cref="AudioDriftLimitMs"/>. The video hold (~0.3 s)
/// is shorter than the decoder's first-PCM latency, so an audio cycle additionally holds until the engine has
/// rendered <see cref="AudioMinContentSeconds"/> of real content — without that the gate passes on an engine that
/// only opened (drift 0.0, no underruns, nothing played: LESSON-TEST-004). Each audio cycle also
/// logs the post-gain output peak per channel (<c>peakL/peakR</c>) so a <c>--audio-gain</c>/<c>--audio-pan</c>/
/// <c>--audio-master</c> run can be checked against unity. After a short warm-up the tracked
/// Metal object count is the baseline and ANY later growth fails the run — growth is an ownership bug,
/// never something to mask (LESSON-BUG-001). A <see cref="Watchdog"/> fails the run loudly if a cycle — or the
/// final teardown — does not complete within <see cref="CycleDeadline"/>, printing the stuck state. Runs on its own thread; the main thread
/// only pumps AppKit. <c>--controller</c> instead drives the app's REAL per-perform path through
/// <see cref="PerformanceController"/> (<see cref="RunController"/>).
/// </summary>
public static class StressHarness
{
    private const int WarmupCycles = 3;
    private const int HoldFramesPerCycle = 30;          // ~0.5s at 60Hz
    private const long WorkingSetGrowthLimitMb = 150;   // same backstop as the Windows harness
    /// <summary>Soak gate: more than this share of an output's presents showing a frame more than one frame period
    /// behind the clock means decode is not keeping up with playback — a FAIL, not a diagnostic.</summary>
    private const double LateFrameFailRatio = 0.10;
    private const long AllocatedGrowthLimitMb = 128;    // the layer's drawable pool (3 × a 4K BGRA surface) may come and go
    private const double AudioDriftLimitMs = 40;       // the Mac runbook's A/V alignment gate for the audio milestone
    private const double AudioMinContentSeconds = 0.25; // every audio cycle must actually PLAY this much before it may exit
    private const double AudioMaxLeadInSeconds = 2.0;   // a fixture may open with encoder-delay/intro silence; past this much content, silence = FAIL
    private const float AudioMinPeak = 0.01f;           // -40 dBFS post-gain: above MP3 dither (Kashmir's lead-in meters 0.0001), below any real content
    private static readonly TimeSpan WedgeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CycleDeadline = TimeSpan.FromSeconds(10);

    public static bool TryParse(string[] args, out StressOptions options)
    {
        options = new StressOptions(50, 1, false, 0, false, false, null, null, false, ShowMode.Span, false, null, false, null, null, null, null, false, true);
        var cycles = 50; var windows = 1; var fullscreen = false; var soak = 0; var sourceCheck = false;
        var hap = false; string? video = null; string? video2 = null; var forceSw = false; var mode = ShowMode.Span;
        var audio = false; string? audioFile = null; var controller = false;
        string? audioDevice = null; double? audioGain = null, audioPan = null, audioMaster = null; var freeRun = false;
        var matchRefresh = true;
        foreach (var arg in args)
        {
            if (arg == "--fullscreen") fullscreen = true;
            else if (arg == "--controller") controller = true;
            else if (arg == "--free-run") freeRun = true;
            else if (arg == "--match-refresh") matchRefresh = true;
            else if (arg == "--no-match-refresh") matchRefresh = false;
            else if (arg == "--audio") audio = true;
            else if (arg.StartsWith("--audio-file=", StringComparison.Ordinal) && arg.Length > "--audio-file=".Length) audioFile = arg["--audio-file=".Length..];
            else if (arg.StartsWith("--audio-device=", StringComparison.Ordinal) && arg.Length > "--audio-device=".Length) audioDevice = arg["--audio-device=".Length..];
            else if (TryParseUnit(arg, "--audio-gain=", 0, 1, out var g)) audioGain = g;
            else if (TryParseUnit(arg, "--audio-pan=", -1, 1, out var pn)) audioPan = pn;
            else if (TryParseUnit(arg, "--audio-master=", 0, 1, out var m)) audioMaster = m;
            else if (arg == "--source-check") sourceCheck = true;
            else if (arg == "--hap") hap = true;
            else if (arg.StartsWith("--video=", StringComparison.Ordinal) && arg.Length > "--video=".Length) video = arg["--video=".Length..];
            else if (arg.StartsWith("--video2=", StringComparison.Ordinal) && arg.Length > "--video2=".Length) video2 = arg["--video2=".Length..];
            else if (arg == "--force-sw-decode") forceSw = true;
            else if (arg == "--mode=span") mode = ShowMode.Span;
            else if (arg == "--mode=individual") mode = ShowMode.Individual;
            else if (arg == "--mode=split") mode = ShowMode.Split;
            else if (arg == "--mode=hap") mode = ShowMode.Hap;
            else if (arg.StartsWith("--cycles=", StringComparison.Ordinal) && int.TryParse(arg["--cycles=".Length..], out var c) && c > 0) cycles = c;
            else if (arg.StartsWith("--windows=", StringComparison.Ordinal) && int.TryParse(arg["--windows=".Length..], out var w) && w > 0) windows = w;
            else if (arg.StartsWith("--soak-seconds=", StringComparison.Ordinal) && int.TryParse(arg["--soak-seconds=".Length..], out var s) && s >= 0) soak = s;
            else return false;
        }
        options = new StressOptions(cycles, windows, fullscreen, soak, sourceCheck, hap, video, video2, forceSw, mode, audio, audioFile, controller,
            audioDevice, audioGain, audioPan, audioMaster, freeRun, matchRefresh);
        return true;
    }

    /// <summary><c>prefix=VALUE</c> as an invariant double inside [min, max]; false (a usage error) otherwise.</summary>
    private static bool TryParseUnit(string arg, string prefix, double min, double max, out double value)
    {
        value = 0;
        return arg.StartsWith(prefix, StringComparison.Ordinal)
            && double.TryParse(arg[prefix.Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max;
    }

    /// <summary>Runs the gate; returns the process exit code (0 = PASS, 1 = FAIL).</summary>
    public static int Run(StressOptions options, ILog log)
    {
        var clipPath = options.Video ?? ResolveHapClip(options, log);
        if (options.Hap && clipPath is null)
        {
            // Never degrade a HAP gate to the test pattern: that would report PASS for a path that never ran.
            log.Error("Stress", "--hap needs a clip: pass --video=<HAP .mov> or set MULTIMON_HAP_FIXTURE=<HAP .mov>.");
            return 2;
        }
        if (options.SourceCheck)
            return SourceCheck.Run(log, clipPath ?? ResolveHapClip(options with { Hap = true }, log), ResolveVideoToolboxClip(options, log));

        var (cycles, windows, fullscreen, soakSeconds, _, requireHap, _, video2, forceSw, mode, _, _, controller, _, _, _, _, freeRunRequested, _) = options;

        // Audio: resolve the track file BEFORE any cycle runs. --audio-file wins; otherwise the video's
        // own audio stream is used, and a video without one FAILS loudly — a silent fallback would report PASS
        // for a path that never played (the LESSON-TEST-004 rule).
        string? audioPath = null;
        if (options.Audio)
        {
            audioPath = options.AudioFile;
            if (audioPath is null)
            {
                if (clipPath is null)
                {
                    log.Error("Stress", "--audio needs --audio-file=<audio file> or --video=<clip with an audio track>.");
                    return 2;
                }
                if (!AudioFileSource.HasAudioTrack(clipPath))
                {
                    log.Error("Stress", $"--audio: '{clipPath}' has no audio track — pass --audio-file=<audio file>.");
                    return 2;
                }
                audioPath = clipPath;
                log.Info("Stress", $"NOTE: --audio without --audio-file — using the audio track of {clipPath}.");
            }
            else if (!File.Exists(audioPath))
            {
                log.Error("Stress", $"--audio-file '{audioPath}' does not exist.");
                return 2;
            }
        }
        else if (options.AudioDevice is not null || options.AudioGain is not null || options.AudioPan is not null || options.AudioMaster is not null)
        {
            log.Error("Stress", "--audio-device/--audio-gain/--audio-pan/--audio-master need --audio.");
            return 2;
        }
        // Device routing: the UID must exist NOW. The engine falls back to the default device for the app (audio
        // over silence); the harness must not — a run that quietly played through another device would PASS a
        // routing path that never ran.
        if (options.AudioDevice is { } requestedDevice)
        {
            var devices = AudioEngine.EnumerateDevices(log);
            var device = devices.FirstOrDefault(d => d.Id == requestedDevice);
            if (device is null)
            {
                log.Error("Stress", $"--audio-device '{requestedDevice}' is not an output device on this machine. Available: " +
                                    (devices.Count == 0 ? "(none)" : string.Join(", ", devices.Select(d => $"'{d.Id}' ({d.Name})"))) + ".");
                return 2;
            }
            log.Info("Stress", $"audio routed to device '{device.Name}' id={device.Id}{(device.IsDefault ? " (the default)" : "")}");
        }
        if (options.AudioGain is not null || options.AudioPan is not null || options.AudioMaster is not null)
            log.Info("Stress", $"audio mix: gain={options.AudioGain?.ToString("0.00") ?? "unity"} pan={options.AudioPan?.ToString("0.00") ?? "centre"} master={options.AudioMaster?.ToString("0.00") ?? "unity"}");
        // Per-source clip list: Individual mode gives output i clip i (the second from --video2, else the first again).
        var perOutput = mode is ShowMode.Individual or ShowMode.Hap; // one source per output; else one shared source
        var clips = clipPath is null ? Array.Empty<string>() : video2 is null ? [clipPath] : [clipPath, video2];
        if (video2 is not null && (clipPath is null || !perOutput))
            log.Info("Stress", "NOTE: --video2 only feeds the second source in --mode=individual|hap with --video — ignored.");
        // --free-run: each Individual-mode source runs on its OWN MasterClock (independent timelines). Every other
        // mode is one shared clock by definition, so the flag is noted and ignored there (the Windows semantics).
        var freeRun = freeRunRequested && mode == ShowMode.Individual && clipPath is not null;
        if (freeRunRequested && !freeRun)
            log.Info("Stress", "NOTE: --free-run only applies to --mode=individual with --video — ignored.");
        log.Info("Stress", $"cycles={cycles} windows={windows} fullscreen={fullscreen} soakSeconds={(soakSeconds > 0 ? soakSeconds.ToString() : "off")} " +
                           $"audio={(audioPath ?? "off")} " +
                           (clipPath is not null ? $"content={string.Join(" + ", clips)} mode={mode} freeRun={freeRun} requireHap={requireHap} forceSwDecode={forceSw} (sources created/started/stopped/disposed per cycle)"
                                                 : "content=test pattern / bound source (alternating cycles)"));

        // Monitors: NSScreen is read on the main thread, exactly as the app will.
        var monitors = MainThread.Invoke(() =>
        {
            using var service = new MonitorService(log);
            return service.GetMonitors();
        });
        log.Info("Stress", $"monitors={monitors.Count}: " + string.Join(" | ", monitors.Select(m => $"{m.DisplayName} {m.Bounds}")));
        if (fullscreen && windows > monitors.Count)
        {
            // A second fullscreen output on the same screen would only cover the first and prove nothing —
            // cap honestly rather than pretend a display exists.
            log.Info("Stress", $"NOTE: requested {windows} fullscreen windows but only {monitors.Count} display(s) attached — capping at {monitors.Count}.");
            windows = monitors.Count;
        }

        // Fullscreen windows take their display's bounds; windowed runs get 960x540 rects tiled in a row on the
        // primary display, non-overlapping, so every requested window is visible on a one-display box.
        // Bounds are FIXED per window so cycling never resizes the layer.
        const int wWidth = 960, wHeight = 540, wGap = 20;
        var bounds = new MonitorRect[windows];
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        for (var i = 0; i < windows; i++)
        {
            bounds[i] = fullscreen ? monitors[i].Bounds : new MonitorRect(120 + i * (wWidth + wGap), 120, wWidth, wHeight);
            if (!fullscreen && !Contains(primary.Bounds, bounds[i]))
                log.Info("Stress", $"NOTE: Output{i + 1} rect {bounds[i]} leaves the primary display {primary.Bounds} — partly or fully off-screen; the compositor may hand it no drawables.");
        }

        if (controller)
            return RunController(log, monitors, bounds, cycles, soakSeconds, mode, clipPath, video2, requireHap, forceSw, freeRun, audioPath, options);

        // Build the persistent pipeline ONCE.
        var provider = new GraphicsDeviceProvider(log);
        provider.Acquire();
        var loop = new RenderLoop(provider, log);
        var clock = new MasterClock();
        loop.Clock = clock;
        var pass = new FullscreenQuadPass(provider); // no source bound = the animated test pattern
        // The sourced half of a pattern run: ONE frame texture uploaded once, ONE bound pass with its persistent
        // source texture (bound once — never rebuilt per cycle), and a timeline the harness publishes a new frame
        // into each sourced cycle, exactly as a decode thread would. Frames wrap the shared texture; their release
        // closure counts, so published == released after teardown proves every frame was disposed exactly once.
        IMTLTexture? frameTexture = null;
        FrameTimeline? sourceTimeline = null;
        FullscreenQuadPass? sourcePass = null;
        if (clipPath is null)
        {
            frameTexture = SourceCheck.CreateQuadrantTexture(provider);
            sourceTimeline = new FrameTimeline();
            sourcePass = new FullscreenQuadPass(provider);
            sourcePass.BindSource(sourceTimeline, SourceCheck.QuadrantTextureSize, SourceCheck.QuadrantTextureSize, MTLPixelFormat.BGRA8Unorm);
        }
        var framesPublished = 0;
        var framesReleased = 0;
        // Clips: per-cycle sources + their passes (a pass is one persistent texture bound to one source, as in the
        // Windows PerformanceController); the counters must all equal cycles × sources-per-cycle at the end.
        var clipSources = new List<IMetalSource>();
        var clipPasses = new List<FullscreenQuadPass>();
        // --free-run (Individual): output i samples source i on its own clock, reset/started/stopped with the shared one.
        var freeRunClocks = freeRun ? Enumerable.Range(0, windows).Select(_ => new MasterClock()).ToArray() : [];
        int sourcesStarted = 0, sourcesStopped = 0, sourcesDisposed = 0, texturesOutstandingAtDispose = 0;
        var contentFail = false;
        // Audio: the engine is built, started, stopped and disposed PER CYCLE — the app's real per-perform path
        // (a rebuilt engine must re-baseline its content position against the already-running clock).
        AudioEngine? audioEngine = null;
        var audioTrack = audioPath is null ? null : new AudioTrack { Name = Path.GetFileNameWithoutExtension(audioPath), SourceFilePath = audioPath, OutputDeviceId = options.AudioDevice };
        var audioGate = new AudioGate(log, cycles, options.AudioDevice, expectSignal: options.AudioGain is not 0 && options.AudioMaster is not 0);
        var outputs = Array.Empty<OutputWindow>();
        var process = Process.GetCurrentProcess();

        var completed = 0;
        var wedged = false;
        long baseline = -1, maxGrowth = 0;
        ulong allocBaseline = 0, allocMax = 0;
        long wsBaseline = 0, wsMax = 0;

        var watchdog = new Watchdog(log, () => "", () => DumpStuckState(log, loop, outputs, provider));

        try
        {
            loop.Start();
            outputs = new OutputWindow[windows];
            for (var i = 0; i < windows; i++)
                outputs[i] = loop.CreateOutputWindow($"Output{i + 1}", bounds[i]);
            watchdog.Start();

            var targets = new long[windows];
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                using var pool = new NSAutoreleasePool(); // this thread's AppKit calls (Show/Hide) return autoreleased objects
                watchdog.BeginCycle(cycle);

                // EnterPerform: show + bind. The device, windows, layers and pipeline are NEVER touched.
                // Clips: the app's per-perform path — new source(s) via the ladder + pass(es) on the persistent device,
                // clock from 0 (PerformanceController.EnterPerform). Pattern runs: even cycles bind the quadrant source
                // pass, publishing one frame (ascending PTS = the paused clock's media time).
                var sourced = clipPath is not null || cycle % 2 == 0;
                var label = clipPath is not null ? $"clip-{mode.ToString().ToLowerInvariant()}" : sourced ? "source" : "pattern";
                var ptsAtStart = Array.Empty<TimeSpan>();
                if (clipPath is not null)
                {
                    var perCycle = perOutput ? windows : 1;
                    for (var i = 0; i < perCycle; i++)
                    {
                        var source = SourceLadder.Open(clips[i % clips.Length], provider, log, requireHap, forceSw, perCycle == 1 ? "clip" : $"clip{i + 1}");
                        clipSources.Add(source);
                        var p = new FullscreenQuadPass(provider);
                        clipPasses.Add(p);
                        p.BindSource(source.Frames, source.Width, source.Height, source.TextureFormat, source.UseYCoCg);
                    }
                    foreach (var source in clipSources)
                    {
                        source.Start();
                        sourcesStarted++;
                    }
                    ptsAtStart = clipSources.Select(s => s.CurrentPts).ToArray();
                    clock.Reset();
                    foreach (var c in freeRunClocks) c.Reset();
                }
                else if (sourced)
                {
                    sourceTimeline!.Publish(new DecodedFrame(frameTexture!, clock.CurrentMediaTime, () => Interlocked.Increment(ref framesReleased)));
                    framesPublished++;
                }
                for (var i = 0; i < windows; i++)
                {
                    outputs[i].Show(bounds[i]);
                    if (clipPath is not null)
                    {
                        var uv = perOutput ? UvRect.Full : mode == ShowMode.Split ? SplitCellUv(i, windows) : UvLayout.Spanning(i, bounds);
                        var source = clipSources[perOutput ? i : 0];
                        outputs[i].SetContent(perOutput ? clipPasses[i] : clipPasses[0], uv, freeRun ? freeRunClocks[i] : null);
                        if (cycle == 1)
                            log.Info("Stress", $"{outputs[i].Name} <- {source.Id} ({source.GetType().Name}) uv={uv} clock={(freeRun ? "own (free-run)" : "shared")}");
                    }
                    else if (sourced)
                        outputs[i].SetContent(sourcePass, UvLayout.Quadrant(i % 2, (i / 2) % 2, 2, 2));
                    else
                        outputs[i].SetContent(pass);
                }

                // EnterPerform, audio half: build + start the engine while the clock is still paused, so every
                // output renders silence until the timeline starts (the controller's order).
                if (audioTrack is not null)
                {
                    audioEngine = new AudioEngine(clock, log, [audioTrack]);
                    // Master is engine-wide state and is applied before Start (the controller's order); the
                    // per-track setters bind to the live pipeline, which exists only after Start — the clock is
                    // still paused here, so no content is rendered before they land.
                    if (options.AudioMaster is { } master)
                        audioEngine.SetMasterVolume(master);
                    audioEngine.Start();
                    if (options.AudioGain is { } gain)
                        audioEngine.SetTrackVolume(audioTrack.Id, gain);
                    if (options.AudioPan is { } pan)
                        audioEngine.SetTrackPan(audioTrack.Id, pan);
                    audioGate.CheckOpened(cycle, AudioSample.From(audioEngine), audioTrack);
                }
                clock.Start();
                foreach (var c in freeRunClocks) c.Start();

                // Hold: every output must complete its frames (concurrently: targets snapshotted first), or this
                // cycle wedged.
                var hold = Stopwatch.StartNew();
                do
                {
                    for (var i = 0; i < windows; i++)
                        targets[i] = outputs[i].PresentCount + HoldFramesPerCycle;
                    for (var i = 0; i < windows; i++)
                    {
                        var output = outputs[i];
                        if (output.WaitForPresentCount(targets[i], WedgeTimeout))
                            continue;
                        wedged = true;
                        log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — {output.Name} completed no frame for {WedgeTimeout.TotalSeconds:0}s");
                        DumpStuckState(log, loop, outputs, provider);
                    }
                    if (soakSeconds > 0 && !wedged)
                        watchdog.Extend(); // a soak is a deliberate dwell, not a stuck cycle
                } while (!wedged && soakSeconds > 0 && hold.Elapsed < TimeSpan.FromSeconds(soakSeconds));

                // Audio hold: the callback must have consumed real content (not just opened and rendered silence)
                // before this cycle may end, or the audio gate below is measuring nothing. Bounded by the wedge timeout.
                if (audioEngine is not null && !wedged)
                    audioGate.HoldForContent(cycle, () => AudioSample.From(audioEngine), watchdog.StopSignal);

                // ExitPerform: pause the clock(s), unbind + hide. Destroys NOTHING of the pipeline.
                clock.Stop();
                foreach (var c in freeRunClocks) c.Stop();
                foreach (var output in outputs)
                {
                    output.SetContent(null);
                    output.Hide();
                }
                foreach (var output in outputs)
                    if (!output.WaitForIdle(WedgeTimeout))
                    {
                        wedged = true;
                        log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — {output.Name} still has command buffers in flight {WedgeTimeout.TotalSeconds:0}s after hide");
                    }

                // ExitPerform, audio half: read the gate counters, then stop (decode joins first, then the AUHAL
                // unit) and dispose — the same producer-before-consumer order as the video sources.
                var audioText = "";
                if (audioEngine is not null)
                {
                    audioText = audioGate.Sample(cycle, AudioSample.From(audioEngine));
                    audioEngine.Stop();
                    audioEngine.Dispose();
                    audioEngine = null;
                }

                // Clips: each must have ADVANCED during the hold (PTS past its start value and at or ahead of
                // the clock the render thread selected against — its OWN clock under --free-run, else the shared one;
                // a stalled or starving decoder fails here), then the per-perform teardown in the controller's order:
                // stop (join) → passes → sources (frame textures).
                var hapText = "";
                if (clipPath is not null)
                {
                    var sharedTime = clock.CurrentMediaTime;
                    var parts = new List<string>();
                    for (var i = 0; i < clipSources.Count; i++)
                    {
                        var source = clipSources[i];
                        var pts = source.CurrentPts;
                        var clockTime = freeRun ? freeRunClocks[i].CurrentMediaTime : sharedTime;
                        var ok = !source.IsFaulted && pts > ptsAtStart[i] && pts >= clockTime;
                        if (!ok)
                        {
                            contentFail = true;
                            log.Error("Stress", $"cycle {cycle:00}/{cycles}: {source.Id} did not advance — pts {ptsAtStart[i].TotalSeconds:0.000}s -> {pts.TotalSeconds:0.000}s, clock {clockTime.TotalSeconds:0.000}s{(freeRun ? " (own)" : "")}, faulted={source.IsFaulted}, decoded={source.DecodedFrames}");
                        }
                        parts.Add($"{source.Id} pts {ptsAtStart[i].TotalSeconds:0.00}->{pts.TotalSeconds:0.00}s{(freeRun ? $" own-clock={clockTime.TotalSeconds:0.00}s" : "")} decoded={source.DecodedFrames}");
                    }
                    hapText = $", clock={sharedTime.TotalSeconds:0.00}s {string.Join(" ", parts)}";
                    var joinWatch = Stopwatch.StartNew();
                    TeardownSources(clipSources, clipPasses, ref sourcesStopped, ref sourcesDisposed, ref texturesOutstandingAtDispose);
                    hapText += $", stop+dispose {joinWatch.Elapsed.TotalMilliseconds:0}ms";
                }

                if (wedged || contentFail || audioGate.Fail is not null)
                    break;
                completed++;

                // Resource sample: the tracked object count is the primary gate (must be identical to the
                // baseline after warm-up); the device's allocated bytes and the process RSS are the backstops.
                var live = provider.Tracker.LiveCount;
                var alloc = provider.CurrentAllocatedSize;
                process.Refresh();
                var ws = process.WorkingSet64;
                string liveText, allocText, wsText;
                if (cycle <= WarmupCycles)
                {
                    baseline = live; allocBaseline = alloc; wsBaseline = ws;
                    liveText = $"tracked={live} (warmup)"; allocText = $"alloc={alloc / (1024 * 1024)}MB"; wsText = $"rss={ws / (1024 * 1024)}MB";
                }
                else
                {
                    var delta = live - baseline;
                    maxGrowth = Math.Max(maxGrowth, delta);
                    allocMax = Math.Max(allocMax, alloc);
                    wsMax = Math.Max(wsMax, ws);
                    liveText = $"tracked={live} (delta {delta:+0;-#})";
                    allocText = $"alloc={alloc / (1024 * 1024)}MB (delta {((long)alloc - (long)allocBaseline) / (1024 * 1024):+0;-#}MB)";
                    wsText = $"rss={ws / (1024 * 1024)}MB (delta {(ws - wsBaseline) / (1024 * 1024):+0;-#}MB)";
                }
                log.Info("Stress", $"cycle {cycle:00}/{cycles} [{label}]: enter->present({HoldFramesPerCycle}f)->exit ok in {watchdog.Elapsed.TotalMilliseconds:0}ms, {liveText}, {allocText}, {wsText}{hapText}{audioText}, {provider.Tracker}");
            }
        }
        catch (Exception ex)
        {
            wedged = true;
            log.Error("Stress", $"harness failed on cycle {watchdog.Cycle}: {ex}");
        }
        finally
        {
            // Teardown, off the main thread, in the CLAUDE.md order: stop the render loop (join, windows closed
            // on the main thread) → dispose the passes → dispose the source (its buffered frames) → release the device.
            // UNDER the watchdog: the render-thread join is unbounded, so the deadline is what turns a teardown wedge
            // into a loud FAIL + exit 1 instead of a silent hang.
            watchdog.BeginTeardown();
            clock.Stop();
            audioEngine?.Stop();   // only non-null after a cycle threw mid-perform
            audioEngine?.Dispose();
            loop.Stop();
            pass.Dispose();
            sourcePass?.Dispose();
            sourceTimeline?.Dispose();
            if (frameTexture is not null)
            {
                frameTexture.Dispose();
                provider.Tracker.TextureDisposed();
            }
            TeardownSources(clipSources, clipPasses, ref sourcesStopped, ref sourcesDisposed, ref texturesOutstandingAtDispose); // only non-empty after a failed cycle
            provider.Release();
            provider.Dispose();
            watchdog.Stop();
            log.Info("Stress", $"teardown complete: {provider.Tracker}");
        }

        var allocGrowthMb = allocBaseline > 0 && allocMax > allocBaseline ? (long)(allocMax - allocBaseline) / (1024 * 1024) : 0;
        var wsGrowthMb = wsBaseline > 0 && wsMax > wsBaseline ? (wsMax - wsBaseline) / (1024 * 1024) : 0;
        var leakFail = maxGrowth > 0;
        var allocFail = allocGrowthMb > AllocatedGrowthLimitMb;
        var wsFail = wsGrowthMb > WorkingSetGrowthLimitMb;
        var frameFail = framesPublished != Volatile.Read(ref framesReleased);
        var expectedSources = clipPath is null ? 0 : cycles * (perOutput ? windows : 1);
        // A pooled texture still held by GPU work when its source was disposed = the fence and the teardown order
        // disagree (the render side had not completed its reads) — an ownership bug, so it fails the run.
        var sourceFail = sourcesStarted != expectedSources || sourcesStopped != expectedSources || sourcesDisposed != expectedSources || texturesOutstandingAtDispose != 0;
        // A/V alignment gate: the audio engine drift-corrects its content position against the SAME MasterClock the
        // render thread selects frames from, so a stream that stayed inside the limit with zero starvation is aligned.
        var audioGateFail = audioPath is not null && audioGate.Failed;
        var passed = completed == cycles && !wedged && !contentFail && !leakFail && !allocFail && !wsFail && !frameFail && !sourceFail && !audioGateFail && provider.Tracker.LiveCount == 0;
        log.Info("Stress", $"cycles={completed}/{cycles} wedges={(wedged ? 1 : 0)} framesCompleted={outputs.Sum(o => o.PresentCount)} " +
                           (clipPath is null ? $"sourceFrames published={framesPublished} released={framesReleased} "
                                            : $"sources started={sourcesStarted} stopped={sourcesStopped} disposed={sourcesDisposed} (expected {expectedSources}) texturesOutstandingAtDispose={texturesOutstandingAtDispose} ") +
                           (audioPath is null ? "" : $"{audioGate.Summary} ") +
                           $"trackedBaseline={baseline} maxGrowth={maxGrowth} allocMaxGrowth={allocGrowthMb}MB rssMaxGrowth={wsGrowthMb}MB trackedAfterTeardown={provider.Tracker.LiveCount}");
        log.Info("Stress", passed
            ? $"RESULT: PASS — all cycles clean, zero tracked-object growth, flat allocation and RSS{(audioPath is null ? "" : $", audio aligned (peakDrift={audioGate.PeakDriftMs:0.0}ms, 0 underruns)")}."
            : $"RESULT: FAIL — {(wedged ? "wedge detected" : contentFail ? "clip source did not advance / faulted" : leakFail ? "tracked Metal object growth (ownership bug)" : allocFail ? $"device allocation growth {allocGrowthMb}MB > {AllocatedGrowthLimitMb}MB" : wsFail ? $"RSS growth {wsGrowthMb}MB > {WorkingSetGrowthLimitMb}MB" : frameFail ? $"source frames published={framesPublished} released={framesReleased} (frame ownership bug)" : sourceFail ? $"sources started={sourcesStarted} stopped={sourcesStopped} disposed={sourcesDisposed}, expected {expectedSources}; {texturesOutstandingAtDispose} pooled texture(s) still held at dispose" : audioGateFail ? $"audio gate ({audioGate.Reason})" : provider.Tracker.LiveCount != 0 ? $"{provider.Tracker.LiveCount} tracked object(s) survived teardown" : "incomplete run")}.");
        return passed ? 0 : 1;
    }

    /// <summary>
    /// <c>--controller</c>: the Windows <c>RunControllerAsync</c> twin — drive the app's REAL per-perform path
    /// (ApplyShow → EnterPerform → present → ExitPerform per cycle through <see cref="PerformanceController"/>,
    /// sources rebuilt every cycle) instead of the bind-once loop (LESSON-TEST-004: the gate must call the path
    /// the user clicks). The controller is constructed ONCE for the run and disposed at the end, off this thread's
    /// caller — the main thread only pumps AppKit. Per cycle: wait for the queue (wedge detector) → the
    /// Performing state → ≥<see cref="HoldFramesPerCycle"/> completed frames on every bound output (held for
    /// <c>--soak-seconds</c> when given), the <see cref="AudioGate"/> content hold with <c>--audio</c> → ExitPerform →
    /// the Idle state via <see cref="IPerformanceController.StateChanged"/> (bounded) → in-flight drain → resource
    /// sample. Any <see cref="IPerformanceController.CommandFailed"/>, a stalled/faulted source, a non-HAP decoder
    /// under <c>--hap</c>, the wrong clock count under <c>--free-run</c>, or an audio gate miss fails the run. The
    /// final <see cref="PerformanceController.Dispose"/> runs UNDER the watchdog too: a teardown wedge fails the
    /// process within <see cref="CycleDeadline"/> instead of hanging it (the Windows App.OnExit forced-exit rule).
    /// </summary>
    private static int RunController(ILog log, IReadOnlyList<MonitorInfo> monitors, MonitorRect[] bounds, int cycles, int soakSeconds,
        ShowMode mode, string? video, string? video2, bool requireHap, bool forceSw, bool freeRun, string? audioPath, StressOptions options)
    {
        log.Info("Stress", "=== CONTROLLER MODE: ApplyShow -> EnterPerform -> present -> ExitPerform per cycle (real per-perform path) ===");

        // One MonitorInfo per requested window, with the harness bounds (windowed or fullscreen) — the
        // controller places its outputs at Monitors[i].Bounds, exactly as it does for real monitors.
        var windows = bounds.Length;
        var infos = new List<MonitorInfo>(windows);
        for (var i = 0; i < windows; i++)
        {
            var real = i < monitors.Count ? monitors[i] : null;
            infos.Add(new MonitorInfo
            {
                DeviceId = real?.DeviceId ?? $"harness-{i + 1}",
                DisplayName = real?.DisplayName ?? $"Harness {i + 1}",
                Bounds = bounds[i],
                WorkArea = bounds[i],
                Resolution = $"{bounds[i].Width}x{bounds[i].Height}",
                IsPrimary = i == 0,
            });
        }

        // The show: no clip = the test pattern on every output; otherwise the Windows harness's show per mode.
        var show = new ShowDefinition { Mode = mode, SyncIndividual = !freeRun };
        if (video is not null)
        {
            if (mode is ShowMode.Individual or ShowMode.Hap)
            {
                for (var i = 0; i < windows; i++)
                    show.Sources.Add(new SourceBinding
                    {
                        SourceId = $"src{i + 1}",
                        FilePath = i == 0 ? video : (video2 ?? video),
                        MonitorDeviceId = infos[i].DeviceId,
                        IsHap = requireHap,
                    });
            }
            else
            {
                show.Sources.Add(new SourceBinding { SourceId = "main", FilePath = video, IsHap = requireHap });
                if (mode == ShowMode.Split)
                {
                    var (rows, cols) = ShowPlanner.AutoGrid(windows);
                    show.WallConfiguration = new VideoWallConfiguration { SourceVideoPath = video, Auto = true, Rows = rows, Columns = cols };
                }
            }
        }
        var audioTrack = audioPath is null ? null : new AudioTrack { Name = Path.GetFileNameWithoutExtension(audioPath), SourceFilePath = audioPath, OutputDeviceId = options.AudioDevice };
        if (audioTrack is not null)
            show.AudioTracks.Add(audioTrack);
        // Free-run only exists in Individual mode (ShowPlanner); every other mode binds the shared clock.
        var expectedFreeRunClocks = freeRun ? show.Sources.Count : 0;
        log.Info("Stress", $"show: mode={mode} sources={show.Sources.Count}{(show.Sources.Count == 0 ? " (test pattern)" : "")} syncIndividual={show.SyncIndividual} (expect {expectedFreeRunClocks} free-run clock(s)) audioTracks={show.AudioTracks.Count} windows={windows} soakSeconds={(soakSeconds > 0 ? soakSeconds.ToString() : "off")} requireHap={requireHap} forceSwDecode={forceSw}");

        var controller = new PerformanceController(infos, log, forceSw) { MatchDisplayRefresh = options.MatchRefresh };
        var failures = 0;
        // Refresh gate: the mode every real display starts in (id, size, rate) is the mode it must be back in after
        // each ExitPerform and after teardown; after each EnterPerform a display the controller reports as matched
        // must actually be running a multiple of its clip's fps (the gate observes the effect, LESSON-TEST-005).
        using var displayModes = new DisplayModeService(log);
        var originalModes = new Dictionary<uint, DisplayModeInfo>();
        foreach (var info in infos)
            if (uint.TryParse(info.DeviceId, out var id) && displayModes.QueryCurrent(id) is { } m)
                originalModes[id] = m;
        log.Info("Stress", $"matchRefresh={(options.MatchRefresh ? "on" : "off")} displays: " + string.Join(" | ", originalModes.Select(kv => $"{kv.Key}: {kv.Value}")));
        var refreshFail = false;
        controller.CommandFailed += m => { Interlocked.Increment(ref failures); log.Error("Stress", $"controller command failed: {m}"); };
        var watchdog = new Watchdog(log, () => $" (controller state={controller.State})",
            () => DumpStuckState(log, controller.Loop, controller.Outputs, controller.Provider));
        // State transitions arrive on the controller's worker; the Idle wait below is event-driven on them.
        var idle = new ManualResetEventSlim(false);
        controller.StateChanged += state =>
        {
            log.Info("Stress", $"cycle {watchdog.Cycle:00}/{cycles}: state -> {state}");
            if (state == PerformState.Idle) idle.Set();
        };

        var process = Process.GetCurrentProcess();
        var completed = 0;
        var wedged = false;
        var contentFail = false;
        var audioGate = new AudioGate(log, cycles, options.AudioDevice, expectSignal: options.AudioGain is not 0 && options.AudioMaster is not 0);
        // The panel's own reader: rates come from PerformanceController.GetStats deltas, computed here rather
        // than by the render thread (the same PerformanceReadout the control panel binds).
        var readout = new PerformanceReadout();
        var lateFail = false;
        long baseline = -1, maxGrowth = 0;
        ulong allocBaseline = 0, allocMax = 0;
        long wsBaseline = 0, wsMax = 0;

        try
        {
            watchdog.Start();
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                using var pool = new NSAutoreleasePool();
                watchdog.BeginCycle(cycle);
                idle.Reset();

                controller.ApplyShow(show);
                // Mix settings through the controller's own setters, queued FIFO behind ApplyShow (the engine they
                // address exists once it has run) and ahead of EnterPerform (the clock is still paused).
                if (audioTrack is not null)
                {
                    if (options.AudioMaster is { } master)
                        controller.SetMasterVolume(master);
                    if (options.AudioGain is { } gain)
                        controller.SetTrackVolume(audioTrack.Id, gain);
                    if (options.AudioPan is { } pan)
                        controller.SetTrackPan(audioTrack.Id, pan);
                }
                controller.EnterPerform();
                if (!controller.WaitForQueue(WedgeTimeout))
                {
                    wedged = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — ApplyShow/EnterPerform did not complete within {WedgeTimeout.TotalSeconds:0}s (controller worker stuck).");
                    break;
                }
                if (controller.State != PerformState.Performing)
                {
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: perform did not start (state={controller.State}).");
                    break;
                }
                var sourcesAtStart = controller.SourceStatus();
                var freeRunClocks = controller.FreeRunClockCount;
                if (cycle == 1)
                    foreach (var s in sourcesAtStart)
                        log.Info("Stress", $"source {s.Id} <- {s.Kind} clock={(freeRunClocks > 0 ? "own (free-run)" : "shared")}");
                // The bound clock count is the proof the requested clock mode ran (a --free-run gate that silently
                // ran on the shared clock would PASS a path that never executed — the LESSON-TEST-004 rule).
                if (freeRunClocks != expectedFreeRunClocks)
                {
                    contentFail = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: {freeRunClocks} free-run clock(s) bound, expected {expectedFreeRunClocks} (syncIndividual={show.SyncIndividual}).");
                    break;
                }
                if (audioTrack is not null)
                    audioGate.CheckOpened(cycle, SampleAudio(controller), audioTrack);
                if (!CheckRefreshMatched(log, cycle, cycles, controller, displayModes, options.MatchRefresh))
                {
                    refreshFail = true;
                    break;
                }
                // Hold: every bound output must keep completing frames; with --soak-seconds the perform is held
                // that long (each pass restarts the watchdog: a soak is a deliberate dwell, not a stuck cycle).
                var hold = Stopwatch.StartNew();
                readout.Reset();
                var statsAtHoldStart = controller.GetStats();
                readout.Read(statsAtHoldStart); // baseline for the per-second figures below
                do
                {
                    if (!controller.WaitForPresentedFrames(HoldFramesPerCycle, WedgeTimeout, out var stalled))
                    {
                        wedged = true;
                        log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — {stalled} completed no frame for {WedgeTimeout.TotalSeconds:0}s.");
                        DumpStuckState(log, controller.Loop, controller.Outputs, controller.Provider);
                        break;
                    }
                    if (soakSeconds > 0)
                        watchdog.Extend();
                } while (soakSeconds > 0 && hold.Elapsed < TimeSpan.FromSeconds(soakSeconds));
                if (wedged)
                    break;

                var statsAtHoldEnd = controller.GetStats();
                var rows = readout.Read(statsAtHoldEnd);
                // Start-up timing, once: how long each output took to put its first frame on screen, and what of
                // that was the display's mode switch (the reason an external panel starts later than the built-in).
                if (cycle == 1)
                    foreach (var row in rows)
                        log.Info("Stress", $"{row.Name}: first frame {row.StartupMs:0.0}ms after EnterPerform " +
                                           $"(mode switch {row.ModeSwitchMs:0.0}ms, show {row.ShowMs:0.0}ms), display {row.RefreshHz:0.##}Hz");
                if (soakSeconds > 0)
                {
                    // Present rate over the soak, per output: on a matched display this is the display's refresh
                    // rate (every vsync presents), e.g. ~100/s for 25 fps content on a 100 Hz panel. A late frame
                    // is a present whose selected frame was more than one frame period behind the clock.
                    var seconds = hold.Elapsed.TotalSeconds;
                    log.Info("Stress", $"cycle {cycle:00}/{cycles}: soak {seconds:0.0}s: " + string.Join(" | ", rows.Select(r =>
                        $"{r.Name} {r.PresentsPerSecond:0.0} present/s decode {r.DecodeFps:0.0} fps ({r.DecodePath}) late {LateFrames(statsAtHoldStart, statsAtHoldEnd, r.Index)}/{Presents(statsAtHoldStart, statsAtHoldEnd, r.Index)}")));
                    lateFail = !CheckLateFrames(log, cycle, cycles, statsAtHoldStart, statsAtHoldEnd);
                    if (lateFail)
                        break;
                }
                if (audioTrack is not null)
                    audioGate.HoldForContent(cycle, () => SampleAudio(controller), watchdog.StopSignal);

                controller.ExitPerform();
                if (!idle.Wait(WedgeTimeout))
                {
                    wedged = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — Idle state not reached within {WedgeTimeout.TotalSeconds:0}s of ExitPerform (state={controller.State}).");
                    break;
                }
                if (!controller.WaitForQueue(WedgeTimeout))
                {
                    wedged = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — ExitPerform did not complete within {WedgeTimeout.TotalSeconds:0}s.");
                    break;
                }
                if (!controller.WaitForOutputsIdle(WedgeTimeout))
                {
                    wedged = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — command buffers still in flight {WedgeTimeout.TotalSeconds:0}s after hide.");
                    break;
                }
                if (!CheckRefreshRestored(log, $"cycle {cycle:00}/{cycles}: after ExitPerform", displayModes, originalModes))
                {
                    refreshFail = true;
                    break;
                }

                // Content: every source must have ADVANCED during the hold (PTS past its start, not faulted) and,
                // under --hap, be the HAP decoder — a fallthrough to VideoToolbox would gate a path that never ran.
                var sources = controller.SourceStatus();
                var parts = new List<string>();
                for (var i = 0; i < sources.Count; i++)
                {
                    var s = sources[i];
                    var ok = !s.Faulted && s.Pts > sourcesAtStart[i].Pts && (!requireHap || s.Kind == nameof(HapSource));
                    if (!ok)
                    {
                        contentFail = true;
                        log.Error("Stress", $"cycle {cycle:00}/{cycles}: {s.Id} ({s.Kind}) failed the content check — pts {sourcesAtStart[i].Pts.TotalSeconds:0.000}s -> {s.Pts.TotalSeconds:0.000}s, faulted={s.Faulted}, decoded={s.Decoded}, requireHap={requireHap}");
                    }
                    parts.Add($"{s.Id} pts {sourcesAtStart[i].Pts.TotalSeconds:0.00}->{s.Pts.TotalSeconds:0.00}s decoded={s.Decoded}");
                }
                var contentText = $", clocks={(freeRunClocks > 0 ? $"free-run x{freeRunClocks}" : "shared")}{(parts.Count > 0 ? $" {string.Join(" ", parts)}" : "")}";
                // Audio sample at the end of the perform. The engine is rebuilt by every ApplyShow and re-baselines
                // its content position, so drift cannot accumulate cycle to cycle here: the drift gate is meaningful
                // only under --soak-seconds (one long perform), where it is sampled after the whole hold.
                var audioText = controller.AudioStatus() is null ? "" : audioGate.Sample(cycle, SampleAudio(controller));

                if (contentFail || audioGate.Fail is not null)
                    break;
                completed++;

                // Resource sample. The previous show is torn down at the START of the next ApplyShow, so each
                // snapshot holds exactly one show's objects — comparable cycle to cycle after the warm-up.
                var live = controller.Tracker.LiveCount;
                var alloc = controller.AllocatedBytes;
                process.Refresh();
                var ws = process.WorkingSet64;
                string liveText, allocText, wsText;
                if (cycle <= WarmupCycles)
                {
                    baseline = live; allocBaseline = alloc; wsBaseline = ws;
                    liveText = $"tracked={live} (warmup)"; allocText = $"alloc={alloc / (1024 * 1024)}MB"; wsText = $"rss={ws / (1024 * 1024)}MB";
                }
                else
                {
                    var delta = live - baseline;
                    maxGrowth = Math.Max(maxGrowth, delta);
                    allocMax = Math.Max(allocMax, alloc);
                    wsMax = Math.Max(wsMax, ws);
                    liveText = $"tracked={live} (delta {delta:+0;-#})";
                    allocText = $"alloc={alloc / (1024 * 1024)}MB (delta {((long)alloc - (long)allocBaseline) / (1024 * 1024):+0;-#}MB)";
                    wsText = $"rss={ws / (1024 * 1024)}MB (delta {(ws - wsBaseline) / (1024 * 1024):+0;-#}MB)";
                }
                log.Info("Stress", $"cycle {cycle:00}/{cycles}: apply->enter->present({HoldFramesPerCycle}f{(soakSeconds > 0 ? $", soak {hold.Elapsed.TotalSeconds:0}s" : "")})->exit->idle ok in {watchdog.Elapsed.TotalMilliseconds:0}ms, {liveText}, {allocText}, {wsText}{contentText}{audioText}, {controller.Tracker}");
            }
        }
        catch (Exception ex)
        {
            wedged = true;
            log.Error("Stress", $"harness failed on cycle {watchdog.Cycle}: {ex}");
        }
        finally
        {
            // Dispose queues the ordered teardown on the controller's worker and joins it — on this (non-main)
            // thread, UNDER the watchdog: the join is unbounded, so the deadline is what turns a teardown wedge into
            // a loud FAIL + exit 1 instead of a silent hang.
            watchdog.BeginTeardown();
            controller.Dispose();
            watchdog.Stop();
            log.Info("Stress", $"teardown complete: {controller.Tracker}");
            if (!CheckRefreshRestored(log, "after teardown", displayModes, originalModes))
                refreshFail = true;
        }

        var allocGrowthMb = allocBaseline > 0 && allocMax > allocBaseline ? (long)(allocMax - allocBaseline) / (1024 * 1024) : 0;
        var wsGrowthMb = wsBaseline > 0 && wsMax > wsBaseline ? (wsMax - wsBaseline) / (1024 * 1024) : 0;
        var leakFail = maxGrowth > 0;
        var allocFail = allocGrowthMb > AllocatedGrowthLimitMb;
        var wsFail = wsGrowthMb > WorkingSetGrowthLimitMb;
        var outstanding = controller.TexturesOutstandingAtDispose;
        var audioGateFail = audioPath is not null && audioGate.Failed;
        var commandFailures = Volatile.Read(ref failures);
        var survivors = controller.Tracker.LiveCount;
        var passed = completed == cycles && !wedged && !contentFail && !leakFail && !allocFail && !wsFail && outstanding == 0 && !audioGateFail && commandFailures == 0 && survivors == 0 && !refreshFail && !lateFail;

        log.Info("Stress", $"controller: cycles={completed}/{cycles} wedges={(wedged ? 1 : 0)} commandFailures={commandFailures} texturesOutstandingAtDispose={outstanding} " +
                           (audioPath is null ? "" : $"{audioGate.Summary} ") +
                           $"trackedBaseline={baseline} maxGrowth={maxGrowth} allocMaxGrowth={allocGrowthMb}MB rssMaxGrowth={wsGrowthMb}MB trackedAfterTeardown={survivors}");
        log.Info("Stress", passed
            ? $"RESULT: PASS — {completed} controller cycles clean, zero tracked-object growth, flat allocation and RSS{(audioPath is null ? "" : $", audio aligned (peakDrift={audioGate.PeakDriftMs:0.0}ms, 0 underruns)")}."
            : $"RESULT: FAIL — {(wedged ? "wedge detected" : commandFailures > 0 ? "controller command failures" : lateFail ? $"late frames over {LateFrameFailRatio:P0} of presents (decode not keeping up)" : refreshFail ? "display refresh gate (see DisplayMode/Stress lines)" : contentFail ? "clip source did not advance / faulted / wrong decoder / wrong clock count" : leakFail ? "tracked Metal object growth (ownership bug)" : allocFail ? $"device allocation growth {allocGrowthMb}MB > {AllocatedGrowthLimitMb}MB" : wsFail ? $"RSS growth {wsGrowthMb}MB > {WorkingSetGrowthLimitMb}MB" : outstanding != 0 ? $"{outstanding} pooled texture(s) still held at dispose" : audioGateFail ? $"audio gate ({audioGate.Reason})" : survivors != 0 ? $"{survivors} tracked object(s) survived teardown" : "incomplete run")}.");
        return passed ? 0 : 1;
    }

    /// <summary>Late frames / presents an output accumulated between two snapshots (the counters are zeroed by
    /// each Show, so the delta is this perform's).</summary>
    private static long LateFrames(PerformanceSnapshot start, PerformanceSnapshot end, int index) =>
        Find(end, index).LateFrames - Find(start, index).LateFrames;

    private static long Presents(PerformanceSnapshot start, PerformanceSnapshot end, int index) =>
        Find(end, index).PresentCount - Find(start, index).PresentCount;

    private static OutputStats Find(PerformanceSnapshot snapshot, int index)
    {
        foreach (var output in snapshot.Outputs)
            if (output.Index == index)
                return output;
        return default;
    }

    /// <summary>The soak's playback gate: an output whose late frames exceed <see cref="LateFrameFailRatio"/> of its
    /// presents was showing a stale frame too often (decode behind the clock), which no present-rate check sees —
    /// the pipeline happily re-presents the frame it already has. Returns false on a FAIL.</summary>
    private static bool CheckLateFrames(ILog log, int cycle, int cycles, PerformanceSnapshot start, PerformanceSnapshot end)
    {
        var ok = true;
        foreach (var output in end.Outputs)
        {
            var presents = Presents(start, end, output.Index);
            var late = LateFrames(start, end, output.Index);
            if (presents <= 0)
                continue;
            var ratio = late / (double)presents;
            if (ratio <= LateFrameFailRatio)
                continue;
            ok = false;
            log.Error("Stress", $"cycle {cycle:00}/{cycles}: {output.Name}: {late} late frame(s) of {presents} presents " +
                                $"({ratio:P1}) exceeds the {LateFrameFailRatio:P0} soak threshold — decode is not keeping up with the clock.");
        }
        return ok;
    }

    /// <summary>After EnterPerform: every display the controller says it matched (or found already matched) must be
    /// running a multiple of the clip's fps RIGHT NOW (queried, not trusted); a reported switch failure is a FAIL; a
    /// "no suitable mode" is honest and passes. With matching off, any recorded match is a FAIL (the path must not run).</summary>
    private static bool CheckRefreshMatched(ILog log, int cycle, int cycles, PerformanceController controller, DisplayModeService displayModes, bool matchRefresh)
    {
        var ok = true;
        var matches = controller.RefreshMatches;
        if (!matchRefresh && matches.Count > 0)
        {
            log.Error("Stress", $"cycle {cycle:00}/{cycles}: --no-match-refresh but the controller recorded {matches.Count} refresh match(es).");
            return false;
        }
        foreach (var m in matches)
        {
            var now = displayModes.QueryCurrent(m.DisplayId);
            var text = $"cycle {cycle:00}/{cycles}: refresh gate output{m.Output + 1} display {m.DisplayId}: {m.Result} for {m.Fps:0.###} fps, now {now?.ToString() ?? "no mode"}";
            switch (m.Result.Outcome)
            {
                case DisplayMatchOutcome.Matched:
                case DisplayMatchOutcome.AlreadyMatched:
                    if (now is { } n && DisplayModeService.IsMultiple(n.RefreshRate, m.Fps) &&
                        n.PixelWidth == m.Result.Before.PixelWidth && n.PixelHeight == m.Result.Before.PixelHeight)
                        log.Info("Stress", text + " ok");
                    else { ok = false; log.Error("Stress", text + " — NOT a multiple of the clip fps at the original pixel size."); }
                    break;
                case DisplayMatchOutcome.NoSuitableMode:
                    log.Info("Stress", text + " (no suitable mode; accepted)");
                    break;
                default:
                    ok = false;
                    log.Error("Stress", text + " — switch FAILED.");
                    break;
            }
        }
        return ok;
    }

    /// <summary>Every display must be back in exactly the mode (IOKit mode id) it started the run in.</summary>
    private static bool CheckRefreshRestored(ILog log, string phase, DisplayModeService displayModes, Dictionary<uint, DisplayModeInfo> originalModes)
    {
        var ok = true;
        foreach (var (id, original) in originalModes)
        {
            var now = displayModes.QueryCurrent(id);
            if (now is { } n && n.ModeId == original.ModeId)
                continue;
            ok = false;
            log.Error("Stress", $"{phase}: display {id} is {now?.ToString() ?? "no mode"}, expected the original {original}.");
        }
        if (ok)
            log.Info("Stress", $"{phase}: displays restored: " + string.Join(" | ", originalModes.Select(kv => $"{kv.Key}: {kv.Value.RefreshRate:0.##}Hz")));
        return ok;
    }

    /// <summary>The controller path's audio sample, read through its harness surface (the same counters the
    /// bind-once path reads off its own engine). Precondition: <see cref="PerformanceController.AudioStatus"/> non-null.</summary>
    private static AudioSample SampleAudio(PerformanceController controller)
    {
        var a = controller.AudioStatus()!.Value;
        return new AudioSample(a.Active, a.PeakDriftMs, a.Underruns, a.Faulted, a.RenderedSeconds, a.PeakLeft, a.PeakRight, a.OpenedDevices);
    }

    /// <summary>
    /// The run watchdog both paths share: a cycle — or the final teardown — that does not finish within
    /// <see cref="CycleDeadline"/> is a wedge: dump the stuck state and fail the process with exit 1. The teardown
    /// joins (render thread, controller worker) are unbounded, so this is what turns a teardown wedge into a loud
    /// FAIL instead of a silent hang after every cycle passed. <paramref name="stuckContext"/> is appended to the
    /// cycle-wedge line; <paramref name="dumpStuckState"/> is guarded (a half-torn-down pipeline may throw).
    /// </summary>
    private sealed class Watchdog(ILog log, Func<string> stuckContext, Action dumpStuckState)
    {
        private readonly Stopwatch _phaseStartedAt = Stopwatch.StartNew();
        private int _cycle;
        private bool _tearingDown;

        /// <summary>Set once teardown has returned; the bounded holds poll it so they end with the run.</summary>
        public ManualResetEventSlim StopSignal { get; } = new(false);

        /// <summary>The cycle in progress (0 = none).</summary>
        public int Cycle => Volatile.Read(ref _cycle);

        /// <summary>Time since the current cycle (or teardown) began.</summary>
        public TimeSpan Elapsed => _phaseStartedAt.Elapsed;

        public void Start() => new Thread(Watch) { Name = "MultiMon.Watchdog", IsBackground = true }.Start();

        /// <summary>Marks the start of <paramref name="cycle"/>: the deadline runs from now.</summary>
        public void BeginCycle(int cycle)
        {
            _phaseStartedAt.Restart();
            Volatile.Write(ref _cycle, cycle);
        }

        /// <summary>Restarts the deadline inside a deliberate dwell (a soak is not a stuck cycle).</summary>
        public void Extend() => _phaseStartedAt.Restart();

        /// <summary>Marks the start of teardown: the deadline runs from now and a miss is a teardown wedge.</summary>
        public void BeginTeardown()
        {
            Volatile.Write(ref _cycle, 0);
            _phaseStartedAt.Restart();
            Volatile.Write(ref _tearingDown, true);
        }

        public void Stop() => StopSignal.Set();

        private void Watch()
        {
            while (!StopSignal.Wait(250))
            {
                var cycle = Cycle;
                var teardown = Volatile.Read(ref _tearingDown);
                if ((cycle == 0 && !teardown) || _phaseStartedAt.Elapsed <= CycleDeadline) continue;
                log.Error("Stress", teardown
                    ? $"WATCHDOG: teardown did not complete within {CycleDeadline.TotalSeconds:0}s — FAIL."
                    : $"WATCHDOG: cycle {cycle} did not complete within {CycleDeadline.TotalSeconds:0}s{stuckContext()} — FAIL.");
                try { dumpStuckState(); }
                catch (Exception ex) { log.Error("Stress", $"  stuck-state dump failed: {ex.Message}"); }
                log.Info("Stress", teardown ? "RESULT: FAIL — teardown wedge." : "RESULT: FAIL — wedge (watchdog).");
                Environment.Exit(1);
            }
        }
    }

    /// <summary>One audio-gate sample — the counters both harness paths feed the same <see cref="AudioGate"/>.</summary>
    private readonly record struct AudioSample(bool Active, double PeakDriftMs, long Underruns, bool Faulted, double RenderedSeconds,
        float PeakLeft, float PeakRight, IReadOnlyList<(string TrackId, string? DeviceUid)> OpenedDevices)
    {
        public static AudioSample From(AudioEngine e) =>
            new(e.Active, e.PeakDriftMs, e.Underruns, e.AnyFaulted, e.RenderedSeconds, e.PeakLeft, e.PeakRight, e.OpenedDevices);
    }

    /// <summary>
    /// The ONE audio gate both paths run (LESSON-TEST-005: a gate that only measures the absence of errors passes on
    /// a dead device). Per cycle the engine must have STARTED on the requested device (<see cref="CheckOpened"/>),
    /// must render at least <see cref="AudioMinContentSeconds"/> of real content before the cycle may end
    /// (<see cref="HoldForContent"/>), and its end-of-perform counters (<see cref="Sample"/>) accumulate into the
    /// run verdict: any fault, any post-prime underrun, or a peak |audio − MasterClock| over
    /// <see cref="AudioDriftLimitMs"/> fails the run. Post-gain peaks are logged so a mix run can be checked against unity.
    /// </summary>
    private sealed class AudioGate(ILog log, int cycles, string? requestedDevice, bool expectSignal)
    {
        /// <summary>The first failure's cause; null = none so far.</summary>
        public string? Fail { get; private set; }
        public double PeakDriftMs { get; private set; }
        public long Underruns { get; private set; }
        public float PeakLeft { get; private set; }
        public float PeakRight { get; private set; }

        public bool Failed => Fail is not null || Underruns > 0 || PeakDriftMs > AudioDriftLimitMs;

        /// <summary>The gate's actual cause, in the order it checks — never a drift number for a faulted run.</summary>
        public string Reason => Fail ?? (Underruns > 0 ? $"{Underruns} post-prime underrun(s)" : $"peakDrift={PeakDriftMs:0.0}ms > {AudioDriftLimitMs}ms");

        public string Summary => $"audio peakDrift={PeakDriftMs:0.0}ms underruns={Underruns} peakL={PeakLeft:0.000} peakR={PeakRight:0.000}";

        /// <summary>Right after the engine started: a pipeline must exist, and sit on the requested device — the
        /// engine falls back to the default device for the app; the gate must not.</summary>
        public void CheckOpened(int cycle, AudioSample s, AudioTrack track)
        {
            if (!s.Active)
            {
                Fail ??= "no audio pipeline started";
                log.Error("Stress", $"cycle {cycle:00}/{cycles}: no audio pipeline started for {track.SourceFilePath}.");
                return;
            }
            var opened = s.OpenedDevices[0].DeviceUid;
            if (cycle == 1)
                log.Info("Stress", $"audio track '{track.Name}' opened on device uid={opened ?? "?"}");
            if (requestedDevice is not null && opened != requestedDevice)
            {
                Fail ??= "wrong audio device";
                log.Error("Stress", $"cycle {cycle:00}/{cycles}: audio opened on device '{opened ?? "?"}', requested '{requestedDevice}'.");
            }
        }

        /// <summary>The content hold: the render callback must have consumed real content (not just opened and rendered
        /// silence) AND, when the run's gain is non-zero, metered a post-gain peak of at least <see cref="AudioMinPeak"/> on some channel before the
        /// cycle may end, or the sample below measures nothing (LESSON-TEST-005: a fixture that opens with encoder-delay
        /// or intro silence — Kashmir's first ~350 ms decodes to exact zeros — makes a content-only hold meter silence and
        /// call it played). A fixture that is still silent after <see cref="AudioMaxLeadInSeconds"/> of content fails.
        /// Bounded by <see cref="WedgeTimeout"/>.</summary>
        public void HoldForContent(int cycle, Func<AudioSample> sample, ManualResetEventSlim stop)
        {
            if (Fail is not null)
                return;
            var hold = Stopwatch.StartNew();
            var s = sample();
            while (!Satisfied(s) && !s.Faulted && s.RenderedSeconds < AudioMaxLeadInSeconds && hold.Elapsed < WedgeTimeout && !stop.Wait(5))
                s = sample();
            if (s.Faulted || Satisfied(s))
                return;
            if (s.RenderedSeconds < AudioMinContentSeconds)
            {
                Fail ??= "audio rendered no content";
                log.Error("Stress", $"cycle {cycle:00}/{cycles}: audio rendered only {s.RenderedSeconds * 1000:0}ms of content in {WedgeTimeout.TotalSeconds:0}s (need {AudioMinContentSeconds * 1000:0}ms).");
            }
            else
            {
                Fail ??= "audio rendered silence";
                log.Error("Stress", $"cycle {cycle:00}/{cycles}: audio rendered {s.RenderedSeconds * 1000:0}ms of content but the post-gain peak stayed below {AudioMinPeak} (peakL={s.PeakLeft:0.0000} peakR={s.PeakRight:0.0000}; gain expected non-zero).");
            }
        }

        private bool Satisfied(AudioSample s) =>
            s.RenderedSeconds >= AudioMinContentSeconds && (!expectSignal || s.PeakLeft >= AudioMinPeak || s.PeakRight >= AudioMinPeak);

        /// <summary>The end-of-perform sample: accumulates the run counters and returns the cycle's log text.</summary>
        public string Sample(int cycle, AudioSample s)
        {
            PeakDriftMs = Math.Max(PeakDriftMs, s.PeakDriftMs);
            Underruns += s.Underruns;
            PeakLeft = Math.Max(PeakLeft, s.PeakLeft);
            PeakRight = Math.Max(PeakRight, s.PeakRight);
            if (s.Faulted)
            {
                Fail ??= "audio decode faulted";
                log.Error("Stress", $"cycle {cycle:00}/{cycles}: audio decode faulted.");
            }
            return $", audio content={s.RenderedSeconds * 1000:0}ms drift={s.PeakDriftMs:0.0}ms underruns={s.Underruns} peakL={s.PeakLeft:0.000} peakR={s.PeakRight:0.000}";
        }
    }

    /// <summary>Split UV for output <paramref name="index"/> of <paramref name="count"/>: the near-square
    /// auto-grid over one source, row-major (2 → left/right halves; 4 → the quadrants). The controller path drives
    /// the same grid through VideoWallConfiguration; the bind-once path derives it here.</summary>
    private static UvRect SplitCellUv(int index, int count)
    {
        var (rows, cols) = ShowPlanner.AutoGrid(count);
        return UvLayout.Quadrant(index / cols, index % cols, rows, cols);
    }

    /// <summary>The HAP clip for <c>--hap</c>: <c>--video</c>, else the MULTIMON_HAP_FIXTURE path (noted). Null when
    /// the run is not a HAP run or no clip was supplied — the caller decides whether that is an error.</summary>
    private static string? ResolveHapClip(StressOptions options, ILog log)
    {
        if (!options.Hap)
            return null;
        if (options.Video is not null)
            return options.Video;
        var fixture = Environment.GetEnvironmentVariable("MULTIMON_HAP_FIXTURE");
        if (string.IsNullOrEmpty(fixture))
            return null;
        log.Info("Stress", $"NOTE: --hap without --video — using the MULTIMON_HAP_FIXTURE clip {fixture}.");
        return fixture;
    }

    /// <summary>The known-colour H.264 clip for the source-check's VideoToolbox check: <c>--video2</c>, else the
    /// MULTIMON_H264_FIXTURE path (noted), else null (the check is skipped, noted).</summary>
    private static string? ResolveVideoToolboxClip(StressOptions options, ILog log)
    {
        if (options.Video2 is not null)
            return options.Video2;
        var fixture = Environment.GetEnvironmentVariable("MULTIMON_H264_FIXTURE");
        if (string.IsNullOrEmpty(fixture))
            return null;
        log.Info("Stress", $"NOTE: --source-check without --video2 — using the MULTIMON_H264_FIXTURE clip {fixture}.");
        return fixture;
    }

    /// <summary>The controller's ExitPerform order for the sources: stop every decode thread (join) → dispose the
    /// passes (their persistent textures) → dispose the sources (timeline frames, then their textures).
    /// <paramref name="outstandingAtDispose"/> accumulates every source's <see cref="IMetalSource.OutstandingAtDispose"/>.</summary>
    private static void TeardownSources(List<IMetalSource> sources, List<FullscreenQuadPass> passes, ref int stopped, ref int disposed, ref int outstandingAtDispose)
    {
        foreach (var source in sources)
        {
            source.Stop();
            stopped++;
        }
        foreach (var p in passes)
            p.Dispose();
        passes.Clear();
        foreach (var source in sources)
        {
            source.Dispose();
            disposed++;
            outstandingAtDispose += source.OutstandingAtDispose;
        }
        sources.Clear();
    }

    private static bool Contains(MonitorRect outer, MonitorRect inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y &&
        inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>Stuck-state dump: is the render thread advancing, what phase is each output in, is the main
    /// thread pumping, and what is allocated.</summary>
    private static void DumpStuckState(ILog log, RenderLoop loop, OutputWindow[] outputs, GraphicsDeviceProvider provider)
    {
        var it1 = loop.Iterations; var ph1 = loop.Phase;
        Thread.Sleep(300); // diagnostic sampling interval, not a fix
        var it2 = loop.Iterations; var ph2 = loop.Phase;
        string mainThread;
        try { MainThread.Invoke(() => { }, TimeSpan.FromSeconds(1)); mainThread = "pumping"; }
        catch (TimeoutException) { mainThread = "STUCK"; }
        log.Error("Stress", $"  render-loop heartbeat: iterations {it1}->{it2} ({(it2 > it1 ? "ADVANCING" : "FROZEN")}), phase {ph1}->{ph2}; main thread {mainThread}; " +
                            $"outputs=[{string.Join(", ", outputs.Select(o => $"{o.Name}: visible={o.Visible} frames={o.PresentCount} phase={o.RenderPhase}"))}]; " +
                            $"{provider.Tracker}; alloc={provider.CurrentAllocatedSize / (1024 * 1024)}MB");
    }
}
