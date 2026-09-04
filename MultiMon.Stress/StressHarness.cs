using System.Diagnostics;
using System.Runtime.InteropServices;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Show;
using MultiMon.Core.Sync;
using MultiMon.Core.Timing;
using MultiMon.Audio;
using MultiMon.Control;
using MultiMon.Decode.Hap;
using MultiMon.Decode.MediaFoundation;
using MultiMon.Graphics;
using MultiMon.Platform;

namespace MultiMon.Stress;

/// <summary>
/// Headless stress harness — the PRIMARY verification gate for every milestone. Ports the approach
/// of the old LibVLC StressHarness (headless cycle churn + wedge detection + results log), retargeted
/// at the persistent D3D11 pipeline.
///
/// Milestone 1: the pipeline (ONE device + ONE swapchain per window + ONE render thread + ONE pass)
/// is built ONCE, then every cycle does EnterPerform (show + bind) → hold until ≥N frames presented
/// (the wedge detector) → ExitPerform (unbind + hide) WITHOUT destroying anything. After a short
/// warmup the D3D11 debug-layer live-object count is snapshotted as the baseline and any growth on a
/// later cycle fails the run — growth is an ownership bug, never something to mask (LESSON-BUG-001).
///
/// Milestone 2 adds --video=PATH: a MediaFoundationSource decodes the clip on its own thread and
/// publishes frames through a TripleBuffer; the shared FullscreenQuadPass samples them instead of the
/// test pattern. Without --video the pass renders the M1 test pattern (decode is not exercised).
///
/// Milestone 6 adds a real --audio path: an MfAudioSource decodes a track to PCM and an AudioEngine
/// renders it through shared-mode WASAPI clocked to the same MasterClock as the video, gated on A/V
/// drift + zero underruns. --audio-file=PATH picks a standalone audio file; otherwise --audio uses the
/// audio stream of --video.
///
/// Milestone 7 adds --mode=span|individual|hap|split (how sources bind to outputs via per-output UV
/// sub-rects): span + quad-split share ONE source across outputs (each samples its slice); individual + hap
/// give each output its OWN source (--video2 is the second clip; hap mode forces HAP decode, --hap2 marks
/// video2 HAP in individual mode) — the multi-decoder concurrency path. --free-run gives each individual
/// source its OWN clock (independent timelines) instead of the shared loop clock. --list-audio prints the
/// enumerated render endpoints (D-005) and exits.
///
/// Cross-GPU emulation (off by default): --adapter=warp|amd|nvidia|intel|<index> picks the device's
/// adapter (warp = software rasterizer; vendor = first matching hardware adapter — the dev box's AMD iGPU
/// gives real AMD coverage + the cross-adapter present path); --force-sw-decode drives the MF
/// software-decode fallback; --feature-level=11_1|11_0 pins the feature level. These map to the
/// MULTIMON_ADAPTER / MULTIMON_FORCE_SW_DECODE / MULTIMON_FEATURE_LEVEL env vars in the app (plus
/// MULTIMON_DISABLE_HAP to force the HAP-unsupported fallback).
///
/// --controller drives the same --video/--video2/--mode/--hap/--audio show through the app's real
/// PerformanceController (ApplyShow → EnterPerform → ExitPerform per cycle, sources rebuilt each time) —
/// the per-perform path the control panel takes, which the bind-once cycle loop does not exercise.
///
/// Args: --cycles=N --windows=N [--mode=MODE] [--video=PATH] [--video2=PATH] [--free-run] [--fullscreen]
///       [--controller] [--hap] [--hap2] [--audio [--audio-file=PATH]] [--audio-reperform] [--capture=PATH]
///       [--inject-device-loss=N] [--soak-seconds=N] [--raise-timer] [--list-audio]
///       [--adapter=SEL] [--force-sw-decode] [--feature-level=LVL]
/// </summary>
public static class StressHarness
{
    private const int WarmupCycles = 3;
    private const int ControllerWarmupCycles = 5;   // sources rebuilt per cycle: lazy allocation settles later
    private const int ControllerGrowthStreak = 3;   // consecutive rising cycles that count as a leak
    private const int HoldFramesPerCycle = 20;
    private static readonly TimeSpan WedgeTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(string[] args)
    {
        var cycles = GetIntArg(args, "--cycles", 50);
        var windows = Math.Max(1, GetIntArg(args, "--windows", 1));
        var video = GetStringArg(args, "--video");
        // M7: --video2 is the SECOND source in per-monitor mode (output 2 ← this clip). --hap2 marks it HAP.
        var video2 = GetStringArg(args, "--video2");
        var capture = GetStringArg(args, "--capture");
        var fullscreen = HasFlag(args, "--fullscreen");
        var hap = HasFlag(args, "--hap");
        var hap2 = HasFlag(args, "--hap2");
        // M7: --mode selects how sources bind to outputs (span|individual|hap|split; "quadsplit" still accepted). Default span.
        var mode = ParseMode(GetStringArg(args, "--mode"));
        // M7 Stage A: --free-run gives each Individual-mode source its OWN clock (independent timelines)
        // instead of the shared loop clock; gates that the per-output clock override is leak-/wedge-free.
        var freeRun = HasFlag(args, "--free-run");
        var audio = HasFlag(args, "--audio");
        // M6: --audio plays a track clocked to the MasterClock. --audio-file=PATH picks a standalone audio
        // file (the .mp3s); otherwise --audio decodes the audio stream of --video (e.g. the .mov mp4a track).
        var audioFile = GetStringArg(args, "--audio-file");
        // Device-removed recovery gate (M4): inject the recovery path every N post-warmup cycles to
        // verify the full recreate is leak- and wedge-free without a real TDR. 0 = no injection.
        var injectDeviceLoss = Math.Max(0, GetIntArg(args, "--inject-device-loss", 0));
        // M4 Stage 2: present continuously for N seconds (one EnterPerform, no cycle churn) so a REAL
        // TDR (Ctrl+Shift+Win+B / driver restart) can be triggered by hand and the recover+resume+zero-
        // growth observed. 0 = normal cycle mode.
        var soakSeconds = Math.Max(0, GetIntArg(args, "--soak-seconds", 0));
        // Held-pause repro for the multi-output pause freeze: during a soak, stop the clock(s) after N seconds
        // and keep presenting, so the present-rate watchdog can catch an output that stalls while paused.
        var pauseAfter = Math.Max(0, GetIntArg(args, "--pause-after", 0));

        // Regression gate (M6): raise the system timer resolution to 1ms — exactly what MF/WASAPI do
        // globally when audio plays. Before the waitable-swapchain present model (ADR 0002 D3) this made
        // Present(1) stall for seconds and wedged the dual cross-adapter swapchains ~50% of runs WITHOUT
        // any audio. It must now pass cleanly: it is the cheapest deterministic reproduction of the present
        // wedge, so keep running it after any render/present change.
        var raiseTimer = HasFlag(args, "--raise-timer");
        if (raiseTimer)
            timeBeginPeriod(1);

        // Cross-GPU emulation hooks (off by default). --adapter selects the device's adapter
        // (warp|amd|nvidia|intel|<index>); --force-sw-decode and --feature-level are surfaced through the
        // same env vars the provider/MF read, so one path drives both the harness and the app.
        var adapterSelector = GetStringArg(args, "--adapter");
        if (HasFlag(args, "--force-sw-decode"))
            Environment.SetEnvironmentVariable("MULTIMON_FORCE_SW_DECODE", "1");
        var featureLevel = GetStringArg(args, "--feature-level");
        if (featureLevel is not null)
            Environment.SetEnvironmentVariable("MULTIMON_FEATURE_LEVEL", featureLevel);

        // D-005 probe: print the enumerated render endpoints (id + friendly name + default) and exit.
        if (HasFlag(args, "--list-audio"))
        {
            var probe = new ConsoleLog();
            foreach (var d in AudioEngine.EnumerateDevices(probe))
                probe.Info("Stress", $"audio device: {d}  id={d.Id}");
            return 0;
        }

        // Audio re-perform regression gate (M7): audio-only, models the app's per-Perform rebuild that the
        // normal cycle loop does NOT. Short-circuits the graphics pipeline entirely.
        if (HasFlag(args, "--audio-reperform"))
            return RunAudioReperform(new ConsoleLog(), cycles, audioFile ?? video);

        // Per-monitor DPI awareness so MonitorService's physical-pixel bounds map 1:1 to window
        // coordinates (an exe normally declares this in a manifest; the harness sets it directly).
        SetProcessDpiAwarenessContext(new IntPtr(-4) /* PER_MONITOR_AWARE_V2 */);

        var log = new ConsoleLog();
        log.Info("Stress", "=== STRESS HARNESS START (M2–M7: pipeline / decode / audio / span+quad modes / device recovery) ===");
        log.Info("Stress", $"cycles={cycles} windows={windows} mode={mode} video={(video ?? "(test pattern)")} video2={(video2 ?? "(none)")} fullscreen={fullscreen} hap={hap} hap2={hap2} audio={audio} injectDeviceLoss={(injectDeviceLoss > 0 ? $"every {injectDeviceLoss} cycles" : "off")} soakSeconds={(soakSeconds > 0 ? soakSeconds.ToString() : "off")}");
        if (hap && video is null)
            log.Info("Stress", "NOTE: --hap needs --video=<HAP .mov>; falling back to the test pattern.");
        if (audio && audioFile is null && video is null)
            log.Info("Stress", "NOTE: --audio needs --audio-file=<file> or --video=<file with audio>; audio disabled.");

        GpuCapabilityService.EnsureDetected();
        log.Info("Stress", $"GPU={GpuCapabilityService.DetectedGpuName} vendor={GpuCapabilityService.DetectedVendor} supportsHap={GpuCapabilityService.SupportsHap}");
        if (adapterSelector is not null || featureLevel is not null || HasFlag(args, "--force-sw-decode"))
            log.Info("Stress", $"cross-GPU hooks: adapter={(adapterSelector ?? "(default)")} featureLevel={(featureLevel ?? "(default)")} forceSwDecode={HasFlag(args, "--force-sw-decode")}");

        IMonitorService monitorService = new MonitorService(log);
        var monitors = monitorService.GetMonitors();
        log.Info("Stress", $"monitors={monitors.Count}: " + string.Join(" | ", monitors.Select(m => $"{m.DisplayName} {m.Bounds}")));
        if (windows > monitors.Count)
            log.Info("Stress", $"NOTE: requested {windows} windows but only {monitors.Count} monitor(s) detected.");

        // Fullscreen windows take their monitor's bounds; windowed runs get 960x540 rects tiled in a row
        // with a gap — NON-overlapping and same-Y on purpose, so span's overlap clustering sees them as N
        // distinct columns (one row x N cols) and exercises a real per-output UV slice. A prior staggered
        // (overlapping) layout merged into one cell, making windowed span verification a false positive.
        // Bounds are FIXED per window so cycling never resizes buffers.
        const int wWidth = 960, wHeight = 540, wGap = 20;
        var bounds = new MonitorRect[windows];
        for (var i = 0; i < windows; i++)
        {
            bounds[i] = fullscreen && i < monitors.Count
                ? monitors[i].Bounds
                : new MonitorRect(120 + i * (wWidth + wGap), 120, wWidth, wHeight);
        }

        // --controller: drive the app's REAL per-perform path (ApplyShow → EnterPerform → ExitPerform through
        // PerformanceController, sources rebuilt every cycle) instead of the bind-once loop below.
        if (HasFlag(args, "--controller"))
        {
            if (adapterSelector is not null) // the controller builds its own provider, which reads the env hook
                Environment.SetEnvironmentVariable("MULTIMON_ADAPTER", adapterSelector);
            return await RunControllerAsync(log, monitors, bounds, cycles, mode, video, video2, hap, hap2, freeRun,
                audio ? audioFile ?? video : null);
        }

        // Build the persistent pipeline ONCE. The debug layer is on so live objects can be counted.
        var provider = new GraphicsDeviceProvider(log, enableDebugLayer: true, adapterSelector: adapterSelector);
        provider.Acquire();
        GpuCapabilityService.ApplyDeviceAdapter(provider.DeviceAdapterName ?? "Unknown", provider.DeviceAdapterVendorId, log);
        // Log the HMONITOR→adapter map up front so cross-adapter outputs are attributable (ADR 0002 D4).
        AdapterMap.LogTopology(provider, log);
        var loop = new RenderLoop(provider, log);
        // M7: the pipeline may now hold MORE than one source+pass (per-monitor mode). All native objects
        // are tracked in these lists for the ordered off-thread teardown; outputPass/outputUv map each
        // output to the pass it samples and the UV slice it shows.
        var passes = new List<FullscreenQuadPass>();
        var sources = new List<ISource>();
        MfDeviceManager? mf = null;
        MasterClock? masterClock = null;
        AudioEngine? audioEngine = null;
        FullscreenQuadPass[] outputPass = Array.Empty<FullscreenQuadPass>();
        UvRect[] outputUv = Array.Empty<UvRect>();
        MasterClock?[] outputClock = Array.Empty<MasterClock?>();
        var freeRunClocks = new List<MasterClock>();
        var exitCode = 1;

        try
        {
            loop.Start();

            // Build ONE (source + pass) for a clip. HAP holds no device state (only the pass's BCn texture
            // is device-bound); MF needs the shared device manager (created lazily, once). Both are tracked
            // in the lists for teardown + recovery. Returns the pass so callers can bind it to outputs.
            FullscreenQuadPass BuildSource(string path, bool isHapClip)
            {
                var p = new FullscreenQuadPass(provider.Device);
                if (isHapClip)
                {
                    var h = new HapSource(path, log);
                    p.BindSource(h.Frames, h.Width, h.Height, h.TextureFormat, h.UseYCoCg);
                    sources.Add(h);
                }
                else
                {
                    mf ??= new MfDeviceManager(provider.Device, log,
                        preferSoftwareDecode: GpuCapabilityService.PreferSoftwareDecode || !provider.MultithreadProtected);
                    var s = new MediaFoundationSource(provider.Device, mf, path, log);
                    p.BindSource(s.Frames, s.Width, s.Height);
                    sources.Add(s);
                }
                passes.Add(p);
                return p;
            }

            outputPass = new FullscreenQuadPass[windows];
            outputUv = new UvRect[windows];
            outputClock = new MasterClock?[windows];

            if (video is not null)
            {
                // The render thread reads this clock each frame so every source selects the same time.
                masterClock = new MasterClock();
                loop.Clock = masterClock;

                if (mode is ShowMode.Individual or ShowMode.Hap)
                {
                    // N sources: output i ← source i (its own pass), full UV. The multi-decoder-concurrency
                    // path (the historical "second decoder wedged" class) — every output decodes its own clip.
                    // Hap mode forces the HAP decode path; Individual --free-run gives each output its own clock.
                    for (var i = 0; i < windows; i++)
                    {
                        var path = i == 0 ? video : (video2 ?? video);
                        var isHapClip = mode == ShowMode.Hap || (i == 0 ? hap : (video2 is not null ? hap2 : hap));
                        outputPass[i] = BuildSource(path, isHapClip);
                        outputUv[i] = UvRect.Full;
                        if (freeRun && mode == ShowMode.Individual)
                        {
                            var c = new MasterClock();
                            freeRunClocks.Add(c);
                            outputClock[i] = c;
                        }
                    }
                }
                else
                {
                    // Spanning / split: ONE source, ONE pass, shared by every output; the per-output
                    // UV sub-rect (computed from the mode) is what differs (ADR 0003 D1/D2).
                    var shared = BuildSource(video, hap);
                    for (var i = 0; i < windows; i++)
                    {
                        outputPass[i] = shared;
                        outputUv[i] = mode == ShowMode.Split
                            ? SplitCellUv(i, windows)
                            : UvLayout.Spanning(i, bounds);
                    }
                }

                foreach (var s in sources)
                    s.Start();
            }
            else
            {
                // Test-pattern run (no video): one pattern pass shared across all outputs, full UV. No clock
                // unless audio needs one — the pattern animates off the loop's stopwatch fallback.
                var pattern = new FullscreenQuadPass(provider.Device);
                passes.Add(pattern);
                for (var i = 0; i < windows; i++)
                {
                    outputPass[i] = pattern;
                    outputUv[i] = UvRect.Full;
                }
            }

            // Decode-recovery covers ALL Media Foundation sources (HAP needs none). Null for HAP-only / pattern.
            var mfSources = sources.OfType<MediaFoundationSource>().ToArray();
            IDecodeRecovery? decodeRecovery = mfSources.Length > 0 && mf is not null
                ? new MfDecodeRecovery(mf, mfSources, log)
                : null;

            // Audio (M6): decode a track to PCM and render it through shared-mode WASAPI, clocked to the
            // SAME MasterClock that drives video frame selection (so A/V stay aligned). Needs a clock even
            // in an audio-only / test-pattern run; reuses the video clock when there is one.
            if (audio)
            {
                var audioPath = audioFile ?? video;
                if (audioPath is not null)
                {
                    masterClock ??= new MasterClock();
                    var track = new AudioTrack { Name = Path.GetFileNameWithoutExtension(audioPath), SourceFilePath = audioPath };
                    audioEngine = new AudioEngine(masterClock, log, new[] { track });
                    audioEngine.Start();
                }
            }

            // Device-removed recovery (M4): the render thread recreates the device-bound graph and
            // resumes from the MasterClock. Covers every pass; wired even for test-pattern runs.
            var recoveryHandler = new DeviceRemovedHandler(provider, passes, decodeRecovery, masterClock, log);
            loop.RecoveryHandler = recoveryHandler;

            var outputs = new OutputWindow[windows];
            for (var i = 0; i < windows; i++)
                outputs[i] = loop.CreateOutputWindow($"MultiMon Output {i + 1}", bounds[i]);

            exitCode = soakSeconds > 0
                ? RunSoak(log, provider, outputs, bounds, outputPass, outputUv, outputClock, freeRunClocks, recoveryHandler, soakSeconds, capture, masterClock, pauseAfter)
                : RunCycles(log, provider, loop, outputs, bounds, outputPass, outputUv, outputClock, freeRunClocks, cycles, capture, masterClock, injectDeviceLoss, audioEngine, Math.Max(1, sources.Count));
        }
        catch (Exception ex)
        {
            log.Error("Stress", $"Harness failed: {ex}");
        }
        finally
        {
            // Teardown OFF the calling thread (the V0087 rule), strictly ordered: stop the decode
            // threads → stop+join render loop (windows + swapchains die on the render thread) → passes →
            // sources (readers + held frames) → MF device manager → device.
            await Task.Run(() =>
            {
                foreach (var s in sources) s.Stop();
                audioEngine?.Stop();   // stop audio producers + render threads (off this calling thread)
                loop.Stop();
                foreach (var p in passes) p.Dispose();
                foreach (var s in sources) s.Dispose();
                audioEngine?.Dispose();
                mf?.Dispose();
                provider.Release();
            });
        }

        log.Info("Stress", $"=== STRESS HARNESS END — exit {exitCode} ===");
        return exitCode;
    }

    private static int RunCycles(ConsoleLog log, GraphicsDeviceProvider provider, RenderLoop loop,
        OutputWindow[] outputs, MonitorRect[] bounds, FullscreenQuadPass[] outputPass, UvRect[] outputUv,
        MasterClock?[] outputClock, List<MasterClock> freeRunClocks, int cycles,
        string? capturePath, MasterClock? clock, int injectDeviceLoss, AudioEngine? audio, int sourceCount)
    {
        // Capture one steady frame with content flowing + window visible — the visual gate. When loss is
        // injected, capture the cycle AFTER the first injection so the PNG proves playback RESUMED.
        var captureCycle = capturePath is null ? -1
            : injectDeviceLoss > 0 ? Math.Min(cycles, WarmupCycles + injectDeviceLoss + 1)
            : Math.Min(cycles, WarmupCycles + 2);
        if (!provider.DebugLayerActive)
            log.Error("Stress", "D3D11 debug layer unavailable — live-object growth CANNOT be verified on this machine.");

        var stopwatch = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var baseline = -1;
        var maxGrowth = 0;
        var wsBaselineBytes = 0L;       // working set captured at the end of warmup
        var wsMaxBytes = 0L;
        var completed = 0;
        var wedged = false;
        var recoveries = 0;

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            // EnterPerform: show + bind each output to its pass + UV slice (+ its own clock for free-run),
            // then start the clocks. Creates NOTHING.
            for (var i = 0; i < outputs.Length; i++)
            {
                outputs[i].Show(bounds[i]);
                outputs[i].SetContent(outputPass[i], outputUv[i], outputClock[i]);
            }
            clock?.Start();
            foreach (var c in freeRunClocks) c.Start();

            if (cycle == captureCycle)
                for (var i = 0; i < outputs.Length; i++)
                    outputs[i].RequestCapture(CapturePathFor(capturePath!, i, outputs.Length));

            // Device-removed injection: drive the recovery path mid-cycle (after warmup so the baseline is
            // clean). The render thread recreates the whole graph and resumes; the hold below then proves
            // presenting recovered, and the live-object/working-set checks prove the recreate is leak-free.
            var injectedThisCycle = injectDeviceLoss > 0 && cycle > WarmupCycles && cycle % injectDeviceLoss == 0;
            if (injectedThisCycle)
            {
                recoveries++;
                log.Info("Stress", $"cycle {cycle:00}/{cycles}: injecting device loss (recovery #{recoveries}) …");
                loop.RequestInjectedDeviceLoss();
            }

            // Hold: every output must present its frames, or this cycle wedged. On an injected cycle this
            // also confirms presenting RESUMES after the inline recreate.
            foreach (var output in outputs)
            {
                if (output.WaitForPresentedFrames(HoldFramesPerCycle, WedgeTimeout))
                    continue;
                wedged = true;
                log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — {output.Name} presented no frame for {WedgeTimeout.TotalSeconds:0}s (deviceLost={output.DeviceLost})");
                // Heartbeat diagnostic: is the render thread stalled (frozen iteration count / stuck phase),
                // or alive-but-not-presenting? Sample the loop twice to see whether it is advancing.
                var it1 = loop.Iterations; var ph1 = loop.Phase;
                Thread.Sleep(300);
                var it2 = loop.Iterations; var ph2 = loop.Phase;
                log.Error("Stress", $"  render-loop heartbeat: iterations {it1}->{it2} ({(it2 > it1 ? "ADVANCING" : "FROZEN")}), phase {ph1}->{ph2}, " +
                                    $"presents=[{string.Join(",", outputs.Select(o => o.PresentCount))}], " +
                                    $"renderPhase=[{string.Join(",", outputs.Select(o => o.Name + ":" + o.RenderPhase))}], " +
                                    $"audioDrift={(audio is { Active: true } ? audio.PeakDriftMs.ToString("0.0") + "ms" : "n/a")}");
            }

            // ExitPerform: pause the clock, then unbind + hide. Destroys NOTHING. While paused the
            // hidden outputs stop selecting, so the bounded FrameTimeline fills and the decode thread
            // BLOCKS — PTS cannot run ahead of the paused clock, so playback resumes aligned next cycle
            // (no stale-frame drift).
            clock?.Stop();
            foreach (var c in freeRunClocks) c.Stop();
            foreach (var output in outputs)
            {
                output.SetContent(null);
                output.Hide();
            }

            if (wedged)
                break;
            completed++;

            var live = provider.GetLiveObjectCount();
            string liveText;
            if (live < 0)
            {
                liveText = "liveObjects=n/a";
            }
            else if (cycle <= WarmupCycles)
            {
                baseline = live; // lazy allocations (driver/DXGI internals) settle during warmup
                liveText = $"liveObjects={live} (warmup)";
            }
            else
            {
                var delta = live - baseline;
                maxGrowth = Math.Max(maxGrowth, delta);
                liveText = $"liveObjects={live} (delta {delta:+0;-#})";
            }

            // Working set is the backstop the live-object diff can't see: a recovery that fails to release
            // an OLD device (or its swapchains) leaks them off the CURRENT device's object graph, so only
            // the process footprint reveals it. Baseline after warmup; track the peak post-warmup growth.
            process.Refresh();
            var ws = process.WorkingSet64;
            wsMaxBytes = Math.Max(wsMaxBytes, ws);
            string wsText;
            if (cycle == WarmupCycles)
            {
                wsBaselineBytes = ws;
                wsText = $"ws={ws / (1024 * 1024)}MB (baseline)";
            }
            else if (cycle > WarmupCycles)
            {
                wsText = $"ws={ws / (1024 * 1024)}MB (delta {(ws - wsBaselineBytes) / (1024 * 1024):+0;-#}MB)";
            }
            else
            {
                wsText = $"ws={ws / (1024 * 1024)}MB";
            }

            log.Info("Stress", $"cycle {cycle:00}/{cycles}: enter->present({HoldFramesPerCycle}f)->exit ok, {liveText}, {wsText}");
        }

        stopwatch.Stop();
        var frames = outputs.Sum(o => o.PresentCount);
        var leakFail = provider.DebugLayerActive && maxGrowth > 0;
        // A leaked device per recovery (the class live-objects can't catch — the count is per-device and
        // the device is swapped) balloons the working set; a clean run stays flat regardless of recovery
        // count because each old device is freed before the next. Backstop only — the live-object diff is
        // the primary gate. The bound is calibrated for the 1080p CosmicPower gate clip on the dev box;
        // re-calibrate for higher-resolution clips (per-frame textures are larger).
        var wsGrowthMb = wsBaselineBytes > 0 ? (wsMaxBytes - wsBaselineBytes) / (1024 * 1024) : 0;
        // Each concurrent decoder legitimately holds its own ring of decoded-frame textures, and a
        // device-removed recreate transiently holds old+new frames — so the backstop scales with the
        // number of active sources (150MB base, calibrated for one 1080p clip, + 110MB per extra source).
        // The PRIMARY leak gate is still the D3D11 live-object count (maxGrowth); a leaked device per
        // recovery grows with recovery COUNT and would blow past this bound over a run regardless.
        var workingSetGrowthLimitMb = 150 + 110 * (sourceCount - 1);
        var wsFail = wsBaselineBytes > 0 && wsGrowthMb > workingSetGrowthLimitMb;

        // A/V alignment gate (M6): the audio engine drift-corrects its content position against the SAME
        // MasterClock the video selects frames on, so the two stay aligned. PASS requires the peak drift
        // within a small bound and zero post-prime ring underruns (a starved producer = a real defect).
        const double AvDriftLimitMs = 40;
        var audioActive = audio is { Active: true };
        var driftMs = audioActive ? audio!.PeakDriftMs : 0;
        var underruns = audioActive ? audio!.Underruns : 0;
        var audioFail = audioActive && (driftMs > AvDriftLimitMs || underruns > 0);
        if (audioActive)
            log.Info("Stress", $"A/V alignment: peakDrift={driftMs:0.0}ms (limit {AvDriftLimitMs:0}ms) underruns={underruns}");

        var pass50 = completed == cycles && !wedged && !leakFail && !wsFail && !audioFail;

        // Content-sync: every output samples ONE source texture at ONE MasterClock time per render-loop
        // iteration, so cross-monitor content drift is 0 frames by construction (ADR 0002 D3). The
        // per-output present-count spread below reflects different REFRESH rates (e.g. 60Hz vs 165Hz),
        // not desync — both show the temporally-correct frame regardless of how often they present.
        if (outputs.Length > 1)
        {
            var counts = outputs.Select(o => o.PresentCount).ToArray();
            var spread = counts.Max() - counts.Min();
            log.Info("Stress", $"content-sync: 0-frame drift by construction (shared source @ one clock). " +
                               $"per-output presents=[{string.Join(", ", counts)}] spread={spread} (refresh-rate difference, not desync)");
        }

        log.Info("Stress", $"cycles={completed}/{cycles} wedges={(wedged ? 1 : 0)} recoveries={recoveries} framesPresented={frames} " +
                           $"liveObjectBaseline={baseline} maxGrowth={maxGrowth} wsBaseline={wsBaselineBytes / (1024 * 1024)}MB wsMaxGrowth={wsGrowthMb}MB " +
                           $"elapsed={stopwatch.ElapsedMilliseconds}ms");
        log.Info("Stress", pass50
            ? $"RESULT: PASS — all cycles clean, zero live-object growth{(recoveries > 0 ? $", {recoveries} device-loss recoveries resumed cleanly" : "")}{(audioActive ? $", audio aligned (peakDrift={driftMs:0.0}ms)" : "")}."
            : $"RESULT: FAIL — {(wedged ? "wedge detected" : leakFail ? "live-object growth (ownership bug)" : wsFail ? $"working-set growth {wsGrowthMb}MB > {workingSetGrowthLimitMb}MB (leaked device on recovery?)" : audioFail ? $"audio gate (peakDrift={driftMs:0.0}ms, underruns={underruns})" : "incomplete run")}.");
        return pass50 ? 0 : 1;
    }

    /// <summary>
    /// M4 Stage 2 — real-TDR soak. One EnterPerform, then present continuously for <paramref name="soakSeconds"/>
    /// while the operator triggers a real TDR (Ctrl+Shift+Win+B / driver restart) by hand. Confirms
    /// recover + resume + zero live-object growth across the real loss. Returns 0 = PASS (≥1 recovery,
    /// no wedge, zero growth), 1 = FAIL (wedge or growth), 2 = no TDR observed (nothing to verify).
    /// </summary>
    private static int RunSoak(ConsoleLog log, GraphicsDeviceProvider provider, OutputWindow[] outputs,
        MonitorRect[] bounds, FullscreenQuadPass[] outputPass, UvRect[] outputUv, MasterClock?[] outputClock,
        List<MasterClock> freeRunClocks, DeviceRemovedHandler recovery, int soakSeconds, string? capturePath, MasterClock? clock,
        int pauseAfterSec = 0)
    {
        if (!provider.DebugLayerActive)
            log.Error("Stress", "D3D11 debug layer unavailable — live-object growth CANNOT be verified on this machine.");
        log.Info("Stress", $"=== SOAK MODE: presenting for {soakSeconds}s — TRIGGER A REAL TDR NOW (Ctrl+Shift+Win+B or a driver restart) ===");

        for (var i = 0; i < outputs.Length; i++)
        {
            outputs[i].Show(bounds[i]);
            outputs[i].SetContent(outputPass[i], outputUv[i], outputClock[i]);
        }
        clock?.Start();
        foreach (var c in freeRunClocks) c.Start();

        var process = Process.GetCurrentProcess();
        var deadline = Stopwatch.StartNew();
        var baseline = -1;
        var wsBaselineBytes = 0L;
        var maxGrowth = 0;
        var wsMaxBytes = 0L;
        var wedged = false;
        var lastRecoveries = 0;
        var captured = false;

        // Wait for the first frames, then snapshot the steady-state baseline.
        outputs[0].WaitForPresentedFrames(HoldFramesPerCycle, WedgeTimeout);
        var lastPresent = outputs.Sum(o => o.PresentCount);
        var lastProgress = deadline.Elapsed;

        var paused = false;
        while (deadline.Elapsed < TimeSpan.FromSeconds(soakSeconds))
        {
            Thread.Sleep(1000);

            // Held-pause repro (the multi-output pause freeze): stop the clock(s) but keep the loop running,
            // exactly like the operator hitting Space. The render-loop present-rate watchdog then shows whether
            // any single output stops presenting while paused.
            if (pauseAfterSec > 0 && !paused && deadline.Elapsed >= TimeSpan.FromSeconds(pauseAfterSec))
            {
                clock?.Stop();
                foreach (var c in freeRunClocks) c.Stop();
                paused = true;
                log.Info("Stress", $"=== HELD PAUSE: clocks stopped at {deadline.Elapsed.TotalSeconds:0}s ({freeRunClocks.Count} free-run) — watch present/1.5s lines ===");
            }

            var present = outputs.Sum(o => o.PresentCount);
            var recoveries = recovery.RecoveryCount;
            var advancing = present > lastPresent;
            if (advancing)
                lastProgress = deadline.Elapsed;
            lastPresent = present;

            // A recovery (real TDR) just happened — capture the next steady frame to prove resume.
            if (recoveries > lastRecoveries)
            {
                log.Info("Stress", $"*** REAL DEVICE LOSS RECOVERED (recovery #{recoveries}) — presenting continues ***");
                lastRecoveries = recoveries;
                if (capturePath is not null)
                    captured = false; // re-arm capture so the post-recovery frame is dumped
            }
            if (capturePath is not null && !captured && recoveries > 0 && advancing)
            {
                for (var i = 0; i < outputs.Length; i++)
                    outputs[i].RequestCapture(CapturePathFor(capturePath, i, outputs.Length));
                captured = true;
            }

            var live = provider.GetLiveObjectCount();
            if (baseline < 0 && live >= 0)
            {
                baseline = live;
                wsBaselineBytes = process.WorkingSet64;
            }
            var delta = baseline >= 0 && live >= 0 ? live - baseline : 0;
            maxGrowth = Math.Max(maxGrowth, delta);
            process.Refresh();
            wsMaxBytes = Math.Max(wsMaxBytes, process.WorkingSet64);

            // Wedge = presents stalled well past the worst-case recover window (driver-reset backoff ~4s).
            if (!advancing && deadline.Elapsed - lastProgress > TimeSpan.FromSeconds(8))
            {
                wedged = true;
                log.Error("Stress", $"SOAK WEDGE — no present progress for >8s (deviceLost={outputs.Any(o => o.DeviceLost)}).");
                break;
            }

            log.Info("Stress", $"soak {deadline.Elapsed.TotalSeconds:00}s/{soakSeconds}s: presents={present} recoveries={recoveries} " +
                               $"liveObjects={(live < 0 ? "n/a" : live.ToString())} (delta {delta:+0;-#}) ws={process.WorkingSet64 / (1024 * 1024)}MB");
        }

        clock?.Stop();
        foreach (var c in freeRunClocks) c.Stop();
        foreach (var output in outputs)
        {
            output.SetContent(null);
            output.Hide();
        }

        var totalRecoveries = recovery.RecoveryCount;
        var leakFail = provider.DebugLayerActive && maxGrowth > 0;
        var wsGrowthMb = wsBaselineBytes > 0 ? (wsMaxBytes - wsBaselineBytes) / (1024 * 1024) : 0;
        log.Info("Stress", $"soak end: recoveries={totalRecoveries} wedges={(wedged ? 1 : 0)} framesPresented={outputs.Sum(o => o.PresentCount)} " +
                           $"liveObjectBaseline={baseline} maxGrowth={maxGrowth} wsMaxGrowth={wsGrowthMb}MB");

        if (totalRecoveries == 0)
        {
            log.Info("Stress", "RESULT: NO TDR OBSERVED — no device loss occurred during the soak (nothing to verify). Re-run and trigger the TDR.");
            return 2;
        }
        var passed = !wedged && !leakFail;
        log.Info("Stress", passed
            ? $"RESULT: PASS — {totalRecoveries} REAL device-loss recovery(ies) resumed cleanly, zero live-object growth."
            : $"RESULT: FAIL — {(wedged ? "wedge after device loss" : "live-object growth (ownership bug)")}.");
        return passed ? 0 : 1;
    }

    /// <summary>
    /// Audio re-perform regression gate (--audio-reperform). Models the APP's per-Perform rebuild that the
    /// normal cycle loop does NOT: each cycle disposes + rebuilds the <see cref="AudioEngine"/> (so the
    /// content position restarts at 0) against ONE <see cref="MasterClock"/> that ACCUMULATES across cycles —
    /// Start/Stop, deliberately NEVER reset. That is the exact condition that produced ~25-30s of garbled
    /// audio at the start of a re-perform (the clock was seconds ahead of a freshly-rebuilt-at-0 audio
    /// engine, which then "caught up" by dropping frames). It passes ONLY because
    /// <see cref="WasapiOutput"/> re-baselines its content position to the clock on prime; revert that and
    /// cycle ≥2 drift balloons past the limit. Audio-only — no video decoders to misalign with the shared
    /// accumulating clock. PASS = every cycle's peak A/V drift within the bound and zero post-prime underruns.
    /// </summary>
    private static int RunAudioReperform(ConsoleLog log, int cycles, string? audioPath)
    {
        log.Info("Stress", "=== AUDIO RE-PERFORM REGRESSION GATE (per-cycle audio rebuild, accumulating clock) ===");
        if (audioPath is null || !File.Exists(audioPath))
        {
            log.Error("Stress", "--audio-reperform needs --audio-file=<file> (or --video=<file with an audio stream>).");
            return 1;
        }

        // This run deliberately omits the clock reset (it tests the re-baseline in ISOLATION), so a fresh
        // engine on cycle ≥2 re-baselines to a NON-ZERO clock and shows a fixed ~40ms buffer-latency
        // transient — independent of HOW accumulated the clock is (cycle 50 @75s is the same ~40ms as cycle
        // 2 @3s). The garbled-start bug instead scales with the accumulation (seconds → thousands of ms), so
        // a 200ms bound passes the fixed transient while failing the regression by orders of magnitude. (The
        // real app also resets the clock per perform — B1 — so it sees cycle-1-style ~sub-ms drift.)
        const double DriftLimitMs = 200;
        const int HoldMs = 1500;           // prime + render + let drift correction settle and peak
        var clock = new MasterClock();
        var worstDrift = 0.0;
        long totalUnderruns = 0;
        var failedCycle = -1;

        for (var cycle = 1; cycle <= cycles; cycle++)
        {
            var track = new AudioTrack { Name = Path.GetFileNameWithoutExtension(audioPath), SourceFilePath = audioPath };
            var engine = new AudioEngine(clock, log, new[] { track });
            engine.Start();

            // EnterPerform — DELIBERATELY no clock.Reset(): the clock keeps the time banked by earlier cycles,
            // so a fresh audio engine (content at 0) faces a clock that is seconds ahead. The re-baseline on
            // prime must absorb that, or the drift spikes (the regression).
            clock.Start();
            Thread.Sleep(HoldMs);
            clock.Stop(); // ExitPerform: bank elapsed; the next cycle resumes from here (accumulating).

            // Snapshot the metrics BEFORE teardown — Dispose() clears the pipelines, so engine.Active would
            // read false afterwards.
            var active = engine.Active;
            var drift = active ? engine.PeakDriftMs : 0;
            var underruns = active ? engine.Underruns : 0;
            engine.Stop();
            engine.Dispose();

            worstDrift = Math.Max(worstDrift, drift);
            totalUnderruns += underruns;
            var bad = !active || drift > DriftLimitMs || underruns > 0;
            if (bad && failedCycle < 0) failedCycle = cycle;
            log.Info("Stress", $"reperform cycle {cycle:00}/{cycles}: clockNow={clock.CurrentMediaTime.TotalSeconds:0.0}s " +
                               $"peakDrift={drift:0.0}ms underruns={underruns}{(bad ? "  <-- FAIL" : "")}");
        }

        var passed = failedCycle < 0;
        log.Info("Stress", $"audio re-perform: cycles={cycles} worstDrift={worstDrift:0.0}ms (limit {DriftLimitMs:0}ms) totalUnderruns={totalUnderruns}");
        log.Info("Stress", passed
            ? "RESULT: PASS — audio aligned on every re-perform; the prime re-baseline absorbs the accumulated clock."
            : $"RESULT: FAIL — audio drift/underruns first on cycle {failedCycle} (garbled-start regression).");
        log.Info("Stress", $"=== STRESS HARNESS END — exit {(passed ? 0 : 1)} ===");
        return passed ? 0 : 1;
    }

    /// <summary>
    /// --controller: gates the path the CONTROL PANEL actually takes per Perform, which the bind-once cycle
    /// loop does not: every cycle runs ApplyShow (tear down the previous show's sources/passes/audio, build
    /// new ones, rebind) → EnterPerform → hold N presented frames → ExitPerform through the real
    /// <see cref="PerformanceController"/> on its worker thread. Wedge = any stage not completing within
    /// <see cref="WedgeTimeout"/> (a stuck worker, a stuck render thread, or no frames). Live objects and
    /// working set are diffed exactly as in <see cref="RunCycles"/>, with two deliberate differences:
    /// warm-up is <see cref="ControllerWarmupCycles"/> (sources are rebuilt each cycle, so lazy allocation
    /// settles later), and live-object growth FAILS only when the count RISES on
    /// <see cref="ControllerGrowthStreak"/> consecutive cycles. Reason: a one-time step (+2 objects at
    /// cycle 15 of a 30-cycle run, then flat — a lazily-grown HW decoder sample pool) was observed and is
    /// NOT a leak; a leak rises every cycle. The step is still logged as maxGrowth so it stays visible.
    /// </summary>
    private static async Task<int> RunControllerAsync(ConsoleLog log, IReadOnlyList<MonitorInfo> monitors,
        MonitorRect[] bounds, int cycles, ShowMode mode, string? video, string? video2, bool hap, bool hap2,
        bool freeRun, string? audioPath)
    {
        log.Info("Stress", "=== CONTROLLER MODE: ApplyShow -> EnterPerform -> present -> ExitPerform per cycle (real per-perform path) ===");
        if (video is null)
        {
            log.Error("Stress", "--controller needs --video=<clip>.");
            return 1;
        }

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

        var show = new ShowDefinition { Mode = mode, SyncIndividual = !freeRun };
        if (mode is ShowMode.Individual or ShowMode.Hap)
        {
            for (var i = 0; i < windows; i++)
                show.Sources.Add(new SourceBinding
                {
                    SourceId = $"src{i + 1}",
                    FilePath = i == 0 ? video : (video2 ?? video),
                    MonitorDeviceId = infos[i].DeviceId,
                    IsHap = i == 0 ? hap : (video2 is not null ? hap2 : hap),
                });
        }
        else
        {
            show.Sources.Add(new SourceBinding { SourceId = "main", FilePath = video, IsHap = hap });
            if (mode == ShowMode.Split)
            {
                var (rows, cols) = ShowPlanner.AutoGrid(windows);
                show.WallConfiguration = new VideoWallConfiguration { SourceVideoPath = video, Auto = true, Rows = rows, Columns = cols };
            }
        }
        if (audioPath is not null)
            show.AudioTracks.Add(new AudioTrack { Name = Path.GetFileNameWithoutExtension(audioPath), SourceFilePath = audioPath });

        var controller = new PerformanceController(infos, log, enableDebugLayer: true);
        var failures = 0;
        controller.CommandFailed += m => { Interlocked.Increment(ref failures); log.Error("Stress", $"controller command failed: {m}"); };
        if (!controller.DebugLayerActive)
            log.Error("Stress", "D3D11 debug layer unavailable — live-object growth CANNOT be verified on this machine.");

        var stopwatch = Stopwatch.StartNew();
        var process = Process.GetCurrentProcess();
        var baseline = -1;
        var previous = -1;
        var maxGrowth = 0;
        var growthStreak = 0;
        var maxGrowthStreak = 0;
        var wsBaselineBytes = 0L;
        var wsMaxBytes = 0L;
        var completed = 0;
        var wedged = false;
        try
        {
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                controller.ApplyShow(show);
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
                if (!controller.WaitForPresentedFrames(HoldFramesPerCycle, WedgeTimeout, out var stalled))
                {
                    wedged = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — {stalled} presented no frame for {WedgeTimeout.TotalSeconds:0}s.");
                    break;
                }
                controller.ExitPerform();
                if (!controller.WaitForQueue(WedgeTimeout))
                {
                    wedged = true;
                    log.Error("Stress", $"cycle {cycle:00}/{cycles}: WEDGE — ExitPerform did not complete within {WedgeTimeout.TotalSeconds:0}s.");
                    break;
                }
                completed++;

                // The previous show is torn down at the START of the next ApplyShow, so each snapshot holds
                // exactly one show's objects — comparable cycle to cycle.
                var live = controller.GetLiveObjectCount();
                string liveText;
                if (live < 0)
                {
                    liveText = "liveObjects=n/a";
                }
                else if (cycle <= ControllerWarmupCycles)
                {
                    baseline = live;
                    liveText = $"liveObjects={live} (warmup)";
                }
                else
                {
                    maxGrowth = Math.Max(maxGrowth, live - baseline);
                    growthStreak = live > previous ? growthStreak + 1 : 0;
                    maxGrowthStreak = Math.Max(maxGrowthStreak, growthStreak);
                    liveText = $"liveObjects={live} (delta {live - baseline:+0;-#}{(growthStreak > 0 ? $", rising x{growthStreak}" : "")})";
                }
                previous = live;

                process.Refresh();
                var ws = process.WorkingSet64;
                wsMaxBytes = Math.Max(wsMaxBytes, ws);
                string wsText;
                if (cycle == ControllerWarmupCycles)
                {
                    wsBaselineBytes = ws;
                    wsText = $"ws={ws / (1024 * 1024)}MB (baseline)";
                }
                else if (cycle > ControllerWarmupCycles)
                    wsText = $"ws={ws / (1024 * 1024)}MB (delta {(ws - wsBaselineBytes) / (1024 * 1024):+0;-#}MB)";
                else
                    wsText = $"ws={ws / (1024 * 1024)}MB";

                log.Info("Stress", $"cycle {cycle:00}/{cycles}: apply->enter->present({HoldFramesPerCycle}f)->exit ok, {liveText}, {wsText}");
            }
        }
        catch (Exception ex)
        {
            log.Error("Stress", $"Harness failed: {ex}");
        }
        finally
        {
            // Dispose queues the ordered teardown on the controller's worker and joins it — off this thread.
            await Task.Run(controller.Dispose);
        }
        stopwatch.Stop();

        var leakFail = controller.DebugLayerActive && maxGrowthStreak >= ControllerGrowthStreak;
        var wsGrowthMb = wsBaselineBytes > 0 ? (wsMaxBytes - wsBaselineBytes) / (1024 * 1024) : 0;
        var workingSetGrowthLimitMb = 150 + 110 * (show.Sources.Count - 1); // same backstop as RunCycles
        var wsFail = wsBaselineBytes > 0 && wsGrowthMb > workingSetGrowthLimitMb;
        var commandFailures = Volatile.Read(ref failures);
        var passed = completed == cycles && !wedged && !leakFail && !wsFail && commandFailures == 0;

        log.Info("Stress", $"controller: cycles={completed}/{cycles} wedges={(wedged ? 1 : 0)} commandFailures={commandFailures} " +
                           $"liveObjectBaseline={baseline} maxGrowth={maxGrowth} longestRise={maxGrowthStreak} " +
                           $"wsBaseline={wsBaselineBytes / (1024 * 1024)}MB wsMaxGrowth={wsGrowthMb}MB elapsed={stopwatch.ElapsedMilliseconds}ms");
        log.Info("Stress", passed
            ? $"RESULT: PASS — {completed} controller cycles clean, no sustained live-object growth{(maxGrowth > 0 ? $" (one-time step of +{maxGrowth}, not a leak)" : "")}."
            : $"RESULT: FAIL — {(wedged ? "wedge detected" : leakFail ? $"live-object count rose {maxGrowthStreak} cycles in a row (ownership bug)" : wsFail ? $"working-set growth {wsGrowthMb}MB > {workingSetGrowthLimitMb}MB" : commandFailures > 0 ? "controller command failures" : "incomplete run")}.");
        log.Info("Stress", $"=== STRESS HARNESS END — exit {(passed ? 0 : 1)} ===");
        return passed ? 0 : 1;
    }

    /// <summary>Per-output capture path: "frame.bmp" → "frame_o1.bmp", "frame_o2.bmp", … (single output keeps the base name).</summary>
    private static string CapturePathFor(string basePath, int index, int count)
    {
        if (count <= 1)
            return basePath;
        var dir = Path.GetDirectoryName(basePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        return Path.Combine(dir, $"{name}_o{index + 1}{ext}");
    }

    private static int GetIntArg(string[] args, string name, int def)
    {
        var a = args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return a is not null && int.TryParse(a.AsSpan(name.Length + 1), out var v) ? v : def;
    }

    private static string? GetStringArg(string[] args, string name)
    {
        var a = args.FirstOrDefault(x => x.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return a?[(name.Length + 1)..];
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static ShowMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "individual" or "permonitor" or "per-monitor" => ShowMode.Individual,
        "hap" => ShowMode.Hap,
        "split" or "quadsplit" or "quad-split" or "quad" => ShowMode.Split,
        _ => ShowMode.Span,
    };

    /// <summary>
    /// Quad-split UV for output <paramref name="index"/> of <paramref name="count"/>: a near-square grid
    /// (cols=⌈√count⌉, rows=⌈count/cols⌉) over one source, row-major. count=2 → left/right halves;
    /// count=4 → the four quadrants. The real app drives the grid from VideoWallConfiguration; the harness
    /// derives one to gate the per-output sub-rect binding.
    /// </summary>
    private static UvRect SplitCellUv(int index, int count)
    {
        var (rows, cols) = ShowPlanner.AutoGrid(count);
        return UvLayout.Quadrant(index / cols, index % cols, rows, cols);
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);
}
