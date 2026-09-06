using System.Diagnostics;
using Foundation;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Sync;
using MultiMon.Core.Timing;
using MultiMon.Decode.Mac.Hap;
using MultiMon.Graphics.Mac;
using MultiMon.Platform.Mac;

namespace MultiMon.Stress.Mac;

/// <summary>How HAP sources bind to outputs: Span = ONE source, each output samples its UvLayout slice;
/// Individual = one source per output, full frame (the multi-decoder concurrency path).</summary>
public enum HapMode { Span, Individual }

public sealed record StressOptions(int Cycles, int Windows, bool Fullscreen, int SoakSeconds, bool SourceCheck,
    bool Hap, string? Video, HapMode Mode);

/// <summary>
/// The Mac twin of <c>MultiMon.Stress.StressHarness</c>'s bind-once loop: build the persistent Metal pipeline
/// ONCE, then churn EnterPerform (show + bind) → hold until frames complete (the wedge detector) → ExitPerform
/// (unbind + hide) WITHOUT destroying anything. Cycles alternate the test pattern with a bound SOURCE (a frame
/// published through <see cref="FrameTimeline"/> each sourced cycle, sampled through a quadrant UV rect) so the
/// decode→render handoff churns under the same growth gate. With <c>--hap --video=CLIP</c> every cycle is the
/// app's real per-perform path instead: create + start HapSource(s) on the persistent device → bind → hold
/// while the clip advances (asserted from each source's PTS against the MasterClock) → unbind → stop (join,
/// off the main thread) → dispose → sample. <c>--mode=span</c> shares ONE source across the outputs through
/// their UvLayout slices; <c>--mode=individual</c> gives each output its own source. After a short warm-up the tracked
/// Metal object count is the baseline and ANY later growth fails the run — growth is an ownership bug,
/// never something to mask (LESSON-BUG-001). A watchdog fails the run loudly if a cycle does not complete
/// within <see cref="CycleDeadline"/>, printing the stuck state. Runs on its own thread; the main thread
/// only pumps AppKit.
/// </summary>
public static class StressHarness
{
    private const int WarmupCycles = 3;
    private const int HoldFramesPerCycle = 30;          // ~0.5s at 60Hz
    private const long WorkingSetGrowthLimitMb = 150;   // same backstop as the Windows harness
    private const long AllocatedGrowthLimitMb = 128;    // the layer's drawable pool (3 × a 4K BGRA surface) may come and go
    private static readonly TimeSpan WedgeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CycleDeadline = TimeSpan.FromSeconds(10);

    public static bool TryParse(string[] args, out StressOptions options)
    {
        options = new StressOptions(50, 1, false, 0, false, false, null, HapMode.Span);
        var cycles = 50; var windows = 1; var fullscreen = false; var soak = 0; var sourceCheck = false;
        var hap = false; string? video = null; var mode = HapMode.Span;
        foreach (var arg in args)
        {
            if (arg == "--fullscreen") fullscreen = true;
            else if (arg == "--source-check") sourceCheck = true;
            else if (arg == "--hap") hap = true;
            else if (arg.StartsWith("--video=", StringComparison.Ordinal) && arg.Length > "--video=".Length) video = arg["--video=".Length..];
            else if (arg == "--mode=span") mode = HapMode.Span;
            else if (arg == "--mode=individual") mode = HapMode.Individual;
            else if (arg.StartsWith("--cycles=", StringComparison.Ordinal) && int.TryParse(arg["--cycles=".Length..], out var c) && c > 0) cycles = c;
            else if (arg.StartsWith("--windows=", StringComparison.Ordinal) && int.TryParse(arg["--windows=".Length..], out var w) && w > 0) windows = w;
            else if (arg.StartsWith("--soak-seconds=", StringComparison.Ordinal) && int.TryParse(arg["--soak-seconds=".Length..], out var s) && s >= 0) soak = s;
            else return false;
        }
        options = new StressOptions(cycles, windows, fullscreen, soak, sourceCheck, hap, video, mode);
        return true;
    }

    /// <summary>Runs the gate; returns the process exit code (0 = PASS, 1 = FAIL).</summary>
    public static int Run(StressOptions options, ILog log)
    {
        var hapPath = ResolveHapClip(options, log);
        if (options.Video is not null && !options.Hap)
        {
            log.Error("Stress", "--video needs --hap on the Mac until VideoToolbox lands (TODO 5).");
            return 2;
        }
        if (options.Hap && hapPath is null)
        {
            // Never degrade a HAP gate to the test pattern: that would report PASS for a path that never ran.
            log.Error("Stress", "--hap needs a clip: pass --video=<HAP .mov> or set MULTIMON_HAP_FIXTURE=<HAP .mov>.");
            return 2;
        }
        if (options.SourceCheck)
            return SourceCheck.Run(log, hapPath ?? ResolveHapClip(options with { Hap = true }, log));

        var (cycles, windows, fullscreen, soakSeconds, _, _, _, mode) = options;
        log.Info("Stress", $"cycles={cycles} windows={windows} fullscreen={fullscreen} soakSeconds={(soakSeconds > 0 ? soakSeconds.ToString() : "off")} " +
                           (hapPath is not null ? $"content=HAP {hapPath} mode={mode} (sources created/started/stopped/disposed per cycle)"
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
        if (hapPath is null)
        {
            frameTexture = SourceCheck.CreateQuadrantTexture(provider);
            sourceTimeline = new FrameTimeline();
            sourcePass = new FullscreenQuadPass(provider);
            sourcePass.BindSource(sourceTimeline, SourceCheck.QuadrantTextureSize, SourceCheck.QuadrantTextureSize, MTLPixelFormat.BGRA8Unorm);
        }
        var framesPublished = 0;
        var framesReleased = 0;
        // HAP: per-cycle sources + their passes (a pass is one persistent texture bound to one source, as in the
        // Windows PerformanceController); the counters must all equal cycles × sources-per-cycle at the end.
        var hapSources = new List<HapSource>();
        var hapPasses = new List<FullscreenQuadPass>();
        int sourcesStarted = 0, sourcesStopped = 0, sourcesDisposed = 0, texturesOutstandingAtDispose = 0;
        var contentFail = false;
        var outputs = Array.Empty<OutputWindow>();
        var process = Process.GetCurrentProcess();

        var completed = 0;
        var wedged = false;
        long baseline = -1, maxGrowth = 0;
        ulong allocBaseline = 0, allocMax = 0;
        long wsBaseline = 0, wsMax = 0;

        // Watchdog: a cycle that does not finish within the deadline is a wedge — dump the stuck state and
        // fail the process (a truly stuck main or render thread would otherwise hang the run forever).
        var currentCycle = 0;
        var cycleStartedAt = Stopwatch.StartNew();
        var watchdogStop = new ManualResetEventSlim(false);
        var watchdog = new Thread(() =>
        {
            while (!watchdogStop.Wait(250))
            {
                var cycle = Volatile.Read(ref currentCycle);
                if (cycle == 0 || cycleStartedAt.Elapsed <= CycleDeadline) continue;
                log.Error("Stress", $"WATCHDOG: cycle {cycle} did not complete within {CycleDeadline.TotalSeconds:0}s — FAIL.");
                DumpStuckState(log, loop, outputs, provider);
                log.Info("Stress", "RESULT: FAIL — wedge (watchdog).");
                Environment.Exit(1);
            }
        }) { Name = "MultiMon.Watchdog", IsBackground = true };

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
                cycleStartedAt.Restart();
                Volatile.Write(ref currentCycle, cycle);

                // EnterPerform: show + bind. The device, windows, layers and pipeline are NEVER touched.
                // HAP: the app's per-perform path — new source(s) + pass(es) on the persistent device, clock from 0
                // (PerformanceController.EnterPerform). Pattern runs: even cycles bind the quadrant source pass,
                // publishing one frame (ascending PTS = the paused clock's media time).
                var sourced = hapPath is not null || cycle % 2 == 0;
                var label = hapPath is not null ? $"hap-{mode.ToString().ToLowerInvariant()}" : sourced ? "source" : "pattern";
                var ptsAtStart = Array.Empty<TimeSpan>();
                if (hapPath is not null)
                {
                    var perCycle = mode == HapMode.Individual ? windows : 1;
                    for (var i = 0; i < perCycle; i++)
                    {
                        var source = new HapSource(hapPath, provider, log, perCycle == 1 ? "hap" : $"hap{i + 1}");
                        hapSources.Add(source);
                        var p = new FullscreenQuadPass(provider);
                        hapPasses.Add(p);
                        p.BindSource(source.Frames, source.Width, source.Height, source.TextureFormat, source.UseYCoCg);
                    }
                    foreach (var source in hapSources)
                    {
                        source.Start();
                        sourcesStarted++;
                    }
                    ptsAtStart = hapSources.Select(s => s.CurrentPts).ToArray();
                    clock.Reset();
                }
                else if (sourced)
                {
                    sourceTimeline!.Publish(new DecodedFrame(frameTexture!, clock.CurrentMediaTime, () => Interlocked.Increment(ref framesReleased)));
                    framesPublished++;
                }
                for (var i = 0; i < windows; i++)
                {
                    outputs[i].Show(bounds[i]);
                    if (hapPath is not null)
                    {
                        var uv = mode == HapMode.Individual ? UvRect.Full : UvLayout.Spanning(i, bounds);
                        outputs[i].SetContent(mode == HapMode.Individual ? hapPasses[i] : hapPasses[0], uv);
                        if (cycle == 1)
                            log.Info("Stress", $"{outputs[i].Name} <- {hapSources[mode == HapMode.Individual ? i : 0].Id} uv={uv}");
                    }
                    else if (sourced)
                        outputs[i].SetContent(sourcePass, UvLayout.Quadrant(i % 2, (i / 2) % 2, 2, 2));
                    else
                        outputs[i].SetContent(pass);
                }
                clock.Start();

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
                        cycleStartedAt.Restart(); // a soak is a deliberate dwell, not a stuck cycle
                } while (!wedged && soakSeconds > 0 && hold.Elapsed < TimeSpan.FromSeconds(soakSeconds));

                // ExitPerform: pause the clock, unbind + hide. Destroys NOTHING of the pipeline.
                clock.Stop();
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

                // HAP: the clip must have ADVANCED during the hold (PTS past its start value and at or ahead of
                // the clock the render thread selected against — a stalled or starving decoder fails here), then the
                // per-perform teardown in the controller's order: stop (join) → passes → sources (pool textures).
                var hapText = "";
                if (hapPath is not null)
                {
                    var clockTime = clock.CurrentMediaTime;
                    var parts = new List<string>();
                    for (var i = 0; i < hapSources.Count; i++)
                    {
                        var source = hapSources[i];
                        var pts = source.CurrentPts;
                        var ok = !source.IsFaulted && pts > ptsAtStart[i] && pts >= clockTime;
                        if (!ok)
                        {
                            contentFail = true;
                            log.Error("Stress", $"cycle {cycle:00}/{cycles}: {source.Id} did not advance — pts {ptsAtStart[i].TotalSeconds:0.000}s -> {pts.TotalSeconds:0.000}s, clock {clockTime.TotalSeconds:0.000}s, faulted={source.IsFaulted}, decoded={source.DecodedFrames}");
                        }
                        parts.Add($"{source.Id} pts {ptsAtStart[i].TotalSeconds:0.00}->{pts.TotalSeconds:0.00}s decoded={source.DecodedFrames}");
                    }
                    hapText = $", clock={clockTime.TotalSeconds:0.00}s {string.Join(" ", parts)}";
                    var joinWatch = Stopwatch.StartNew();
                    TeardownHapSources(hapSources, hapPasses, ref sourcesStopped, ref sourcesDisposed, ref texturesOutstandingAtDispose);
                    hapText += $", stop+dispose {joinWatch.Elapsed.TotalMilliseconds:0}ms";
                }

                if (wedged || contentFail)
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
                log.Info("Stress", $"cycle {cycle:00}/{cycles} [{label}]: enter->present({HoldFramesPerCycle}f)->exit ok in {cycleStartedAt.Elapsed.TotalMilliseconds:0}ms, {liveText}, {allocText}, {wsText}{hapText}, {provider.Tracker}");
            }
        }
        catch (Exception ex)
        {
            wedged = true;
            log.Error("Stress", $"harness failed on cycle {Volatile.Read(ref currentCycle)}: {ex}");
        }
        finally
        {
            Volatile.Write(ref currentCycle, 0);
            watchdogStop.Set();
            // Teardown, off the main thread, in the CLAUDE.md order: stop the render loop (join, windows closed
            // on the main thread) → dispose the passes → dispose the source (its buffered frames) → release the device.
            clock.Stop();
            loop.Stop();
            pass.Dispose();
            sourcePass?.Dispose();
            sourceTimeline?.Dispose();
            if (frameTexture is not null)
            {
                frameTexture.Dispose();
                provider.Tracker.TextureDisposed();
            }
            TeardownHapSources(hapSources, hapPasses, ref sourcesStopped, ref sourcesDisposed, ref texturesOutstandingAtDispose); // only non-empty after a failed cycle
            provider.Release();
            provider.Dispose();
            log.Info("Stress", $"teardown complete: {provider.Tracker}");
        }

        var allocGrowthMb = allocBaseline > 0 && allocMax > allocBaseline ? (long)(allocMax - allocBaseline) / (1024 * 1024) : 0;
        var wsGrowthMb = wsBaseline > 0 && wsMax > wsBaseline ? (wsMax - wsBaseline) / (1024 * 1024) : 0;
        var leakFail = maxGrowth > 0;
        var allocFail = allocGrowthMb > AllocatedGrowthLimitMb;
        var wsFail = wsGrowthMb > WorkingSetGrowthLimitMb;
        var frameFail = framesPublished != Volatile.Read(ref framesReleased);
        var expectedSources = hapPath is null ? 0 : cycles * (mode == HapMode.Individual ? windows : 1);
        // A pooled texture still held by GPU work when its source was disposed = the fence and the teardown order
        // disagree (the render side had not completed its reads) — an ownership bug, so it fails the run.
        var sourceFail = sourcesStarted != expectedSources || sourcesStopped != expectedSources || sourcesDisposed != expectedSources || texturesOutstandingAtDispose != 0;
        var passed = completed == cycles && !wedged && !contentFail && !leakFail && !allocFail && !wsFail && !frameFail && !sourceFail && provider.Tracker.LiveCount == 0;
        log.Info("Stress", $"cycles={completed}/{cycles} wedges={(wedged ? 1 : 0)} framesCompleted={outputs.Sum(o => o.PresentCount)} " +
                           (hapPath is null ? $"sourceFrames published={framesPublished} released={framesReleased} "
                                            : $"sources started={sourcesStarted} stopped={sourcesStopped} disposed={sourcesDisposed} (expected {expectedSources}) texturesOutstandingAtDispose={texturesOutstandingAtDispose} ") +
                           $"trackedBaseline={baseline} maxGrowth={maxGrowth} allocMaxGrowth={allocGrowthMb}MB rssMaxGrowth={wsGrowthMb}MB trackedAfterTeardown={provider.Tracker.LiveCount}");
        log.Info("Stress", passed
            ? "RESULT: PASS — all cycles clean, zero tracked-object growth, flat allocation and RSS."
            : $"RESULT: FAIL — {(wedged ? "wedge detected" : contentFail ? "HAP source did not advance / faulted" : leakFail ? "tracked Metal object growth (ownership bug)" : allocFail ? $"device allocation growth {allocGrowthMb}MB > {AllocatedGrowthLimitMb}MB" : wsFail ? $"RSS growth {wsGrowthMb}MB > {WorkingSetGrowthLimitMb}MB" : frameFail ? $"source frames published={framesPublished} released={framesReleased} (frame ownership bug)" : sourceFail ? $"sources started={sourcesStarted} stopped={sourcesStopped} disposed={sourcesDisposed}, expected {expectedSources}; {texturesOutstandingAtDispose} pooled texture(s) still held at dispose" : provider.Tracker.LiveCount != 0 ? $"{provider.Tracker.LiveCount} tracked object(s) survived teardown" : "incomplete run")}.");
        return passed ? 0 : 1;
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

    /// <summary>The controller's ExitPerform order for the sources: stop every decode thread (join) → dispose the
    /// passes (their persistent textures) → dispose the sources (timeline frames, then pool textures).
    /// <paramref name="outstandingAtDispose"/> accumulates every source's <see cref="HapSource.OutstandingAtDispose"/>.</summary>
    private static void TeardownHapSources(List<HapSource> sources, List<FullscreenQuadPass> passes, ref int stopped, ref int disposed, ref int outstandingAtDispose)
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
