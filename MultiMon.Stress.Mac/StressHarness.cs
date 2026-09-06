using System.Diagnostics;
using Foundation;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Sync;
using MultiMon.Core.Timing;
using MultiMon.Graphics.Mac;
using MultiMon.Platform.Mac;

namespace MultiMon.Stress.Mac;

public sealed record StressOptions(int Cycles, int Windows, bool Fullscreen, int SoakSeconds, bool SourceCheck);

/// <summary>
/// The Mac twin of <c>MultiMon.Stress.StressHarness</c>'s bind-once loop: build the persistent Metal pipeline
/// ONCE, then churn EnterPerform (show + bind) → hold until frames complete (the wedge detector) → ExitPerform
/// (unbind + hide) WITHOUT destroying anything. Cycles alternate the test pattern with a bound SOURCE (a frame
/// published through <see cref="FrameTimeline"/> each sourced cycle, sampled through a quadrant UV rect) so the
/// decode→render handoff churns under the same growth gate. After a short warm-up the tracked
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
        options = new StressOptions(50, 1, false, 0, false);
        var cycles = 50; var windows = 1; var fullscreen = false; var soak = 0; var sourceCheck = false;
        foreach (var arg in args)
        {
            if (arg == "--fullscreen") fullscreen = true;
            else if (arg == "--source-check") sourceCheck = true;
            else if (arg.StartsWith("--cycles=", StringComparison.Ordinal) && int.TryParse(arg["--cycles=".Length..], out var c) && c > 0) cycles = c;
            else if (arg.StartsWith("--windows=", StringComparison.Ordinal) && int.TryParse(arg["--windows=".Length..], out var w) && w > 0) windows = w;
            else if (arg.StartsWith("--soak-seconds=", StringComparison.Ordinal) && int.TryParse(arg["--soak-seconds=".Length..], out var s) && s >= 0) soak = s;
            else return false;
        }
        options = new StressOptions(cycles, windows, fullscreen, soak, sourceCheck);
        return true;
    }

    /// <summary>Runs the gate; returns the process exit code (0 = PASS, 1 = FAIL).</summary>
    public static int Run(StressOptions options, ILog log)
    {
        if (options.SourceCheck)
            return SourceCheck.Run(log);

        var (cycles, windows, fullscreen, soakSeconds, _) = options;
        log.Info("Stress", $"cycles={cycles} windows={windows} fullscreen={fullscreen} soakSeconds={(soakSeconds > 0 ? soakSeconds.ToString() : "off")} content=test pattern / bound source (alternating cycles)");

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
        // The sourced half: ONE frame texture uploaded once, ONE bound pass with its persistent source texture
        // (bound once — never rebuilt per cycle), and a timeline the harness publishes a new frame into each
        // sourced cycle, exactly as a decode thread would. Frames wrap the shared texture; their release closure
        // counts, so published == released after teardown proves every frame was disposed exactly once.
        var frameTexture = SourceCheck.CreateQuadrantTexture(provider);
        var sourceTimeline = new FrameTimeline();
        var sourcePass = new FullscreenQuadPass(provider);
        sourcePass.BindSource(sourceTimeline, SourceCheck.QuadrantTextureSize, SourceCheck.QuadrantTextureSize, MTLPixelFormat.BGRA8Unorm);
        var framesPublished = 0;
        var framesReleased = 0;
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

                // EnterPerform: show + bind. Creates NOTHING. Even cycles bind the source pass: publish one frame
                // (ascending PTS = the paused clock's media time) and sample it through a quadrant sub-rect.
                var sourced = cycle % 2 == 0;
                if (sourced)
                {
                    sourceTimeline.Publish(new DecodedFrame(frameTexture, clock.CurrentMediaTime, () => Interlocked.Increment(ref framesReleased)));
                    framesPublished++;
                }
                for (var i = 0; i < windows; i++)
                {
                    outputs[i].Show(bounds[i]);
                    if (sourced)
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

                // ExitPerform: pause the clock, unbind + hide. Destroys NOTHING.
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

                if (wedged)
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
                log.Info("Stress", $"cycle {cycle:00}/{cycles} [{(sourced ? "source" : "pattern")}]: enter->present({HoldFramesPerCycle}f)->exit ok in {cycleStartedAt.Elapsed.TotalMilliseconds:0}ms, {liveText}, {allocText}, {wsText}, {provider.Tracker}");
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
            sourcePass.Dispose();
            sourceTimeline.Dispose();
            frameTexture.Dispose();
            provider.Tracker.TextureDisposed();
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
        var passed = completed == cycles && !wedged && !leakFail && !allocFail && !wsFail && !frameFail && provider.Tracker.LiveCount == 0;
        log.Info("Stress", $"cycles={completed}/{cycles} wedges={(wedged ? 1 : 0)} framesCompleted={outputs.Sum(o => o.PresentCount)} " +
                           $"sourceFrames published={framesPublished} released={framesReleased} " +
                           $"trackedBaseline={baseline} maxGrowth={maxGrowth} allocMaxGrowth={allocGrowthMb}MB rssMaxGrowth={wsGrowthMb}MB trackedAfterTeardown={provider.Tracker.LiveCount}");
        log.Info("Stress", passed
            ? "RESULT: PASS — all cycles clean, zero tracked-object growth, flat allocation and RSS."
            : $"RESULT: FAIL — {(wedged ? "wedge detected" : leakFail ? "tracked Metal object growth (ownership bug)" : allocFail ? $"device allocation growth {allocGrowthMb}MB > {AllocatedGrowthLimitMb}MB" : wsFail ? $"RSS growth {wsGrowthMb}MB > {WorkingSetGrowthLimitMb}MB" : frameFail ? $"source frames published={framesPublished} released={framesReleased} (frame ownership bug)" : provider.Tracker.LiveCount != 0 ? $"{provider.Tracker.LiveCount} tracked object(s) survived teardown" : "incomplete run")}.");
        return passed ? 0 : 1;
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
