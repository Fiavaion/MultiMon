using System.Collections.Concurrent;
using System.Diagnostics;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;

namespace MultiMon.Graphics;

/// <summary>
/// The single render/present thread — created once, alive for the whole session. It exclusively
/// owns everything thread-affine in the output path: the immediate context (ALL rendering +
/// Present), the output HWNDs (created, message-pumped, and destroyed here), and the window list.
/// Other threads interact only via <see cref="Invoke"/>, which marshals a command onto this thread
/// and blocks until it ran — the calling/UI thread never touches the context or an HWND.
///
/// Stop() is the ONLY teardown, runs off the UI thread, and is strictly ordered: signal stop →
/// loop exits → windows disposed ON the render thread (RTV → swapchain → HWND) → thread joined.
/// The caller releases the device after Join returns (graphics-core disposal-order rule).
/// </summary>
public sealed class RenderLoop
{
    private sealed class Command
    {
        public required Action Action { get; init; }
        public ManualResetEventSlim Done { get; } = new(false);
        public Exception? Error { get; set; }
    }

    private readonly GraphicsDeviceProvider _provider;
    private readonly ILog _log;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<OutputWindow> _windows = new(); // render-thread-owned

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _threadExited;
    private volatile bool _injectLossRequested;
    private int _renderThreadId = -1;

    // Wedge diagnostics (M6): a heartbeat the harness samples to tell a stalled render thread (frozen
    // iteration count / stuck phase) from one that is merely not presenting. Cheap volatile writes.
    private long _iterations;
    private volatile string _phase = "init";
    public long Iterations => Volatile.Read(ref _iterations);
    public string Phase => _phase;

    // Present-rate watchdog (pause-freeze diagnostic): every ~1.5s while any output is visible, log each
    // output's present delta. A visible output whose delta is 0 while others advance is a stalled output —
    // the signature of the multi-output pause freeze. Cheap: a few thread-safe reads + one throttled log line.
    private readonly Dictionary<string, long> _diagLastPresent = new();
    private TimeSpan _nextDiagAt = TimeSpan.Zero;
    private long _latencyTimeouts;

    public RenderLoop(GraphicsDeviceProvider provider, ILog log)
    {
        _provider = provider;
        _log = log;
    }

    /// <summary>
    /// The playback timeline the render thread reads each frame to drive source frame selection
    /// (ADR 0002 D1). When null (e.g. test-pattern runs) the loop falls back to its own stopwatch so
    /// the animated pattern still moves. Set once before perform mode; read on the render thread.
    /// </summary>
    public MasterClock? Clock { get; set; }

    /// <summary>
    /// Device-removed recovery (Milestone 4). When set, the render thread runs it inline whenever an
    /// output reports a device loss on Present (or an injected loss is requested), recreating the
    /// device-bound graph and resuming. Set once before perform mode; read on the render thread.
    /// </summary>
    public DeviceRemovedHandler? RecoveryHandler { get; set; }

    public bool IsRunning => _thread is not null;

    /// <summary>
    /// Stress-harness hook: drive the device-removed recovery path at the next render-loop iteration
    /// WITHOUT a real TDR, to verify the recreate is leak- and wedge-free. Safe to call from any thread.
    /// </summary>
    public void RequestInjectedDeviceLoss() => _injectLossRequested = true;

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("RenderLoop is already running.");
        _stopRequested = false;
        _threadExited = false;
        _thread = new Thread(ThreadProc) { Name = "MultiMon.Render", IsBackground = false };
        _thread.Start();
        _log.Info("Graphics", "Render loop started");
    }

    /// <summary>
    /// Stops the loop and joins the thread. The render thread disposes all output windows
    /// (swapchains included) as its final act, so when this returns the device has no live
    /// swapchains and the caller may Release() it. Never call from the render thread itself.
    /// </summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
            return;
        if (Environment.CurrentManagedThreadId == _renderThreadId)
            throw new InvalidOperationException("RenderLoop.Stop must not be called from the render thread.");

        _stopRequested = true;
        _wake.Set();
        thread.Join();
        _thread = null;
        _log.Info("Graphics", "Render loop stopped and joined; all output windows disposed on the render thread.");
    }

    /// <summary>Creates a persistent output window ON the render thread (HWNDs are thread-affine).</summary>
    public OutputWindow CreateOutputWindow(string name, MonitorRect initialBounds)
        => Invoke(() =>
        {
            var window = new OutputWindow(_provider, this, _log, name, initialBounds);
            _windows.Add(window);
            return window;
        });

    /// <summary>Runs <paramref name="action"/> on the render thread and blocks until it completed.</summary>
    public void Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _renderThreadId)
        {
            action();
            return;
        }
        if (_thread is null)
            throw new InvalidOperationException("RenderLoop is not running.");

        var command = new Command { Action = action };
        _commands.Enqueue(command);
        _wake.Set();
        // Re-check the exited flag while waiting: if the render thread died between our enqueue and
        // its final command drain, the command will never run — fail loudly instead of wedging.
        while (!command.Done.Wait(100))
        {
            if (_threadExited)
                throw new InvalidOperationException("Render thread exited before the command ran.");
        }
        if (command.Error is not null)
            throw new InvalidOperationException($"Render-thread command failed: {command.Error.Message}", command.Error);
    }

    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        // Block body so the lambda is void-returning and binds to Invoke(Action) — an expression
        // body would return T and recurse into this overload.
        Invoke(() => { result = func(); });
        return result;
    }

    private void ThreadProc()
    {
        _renderThreadId = Environment.CurrentManagedThreadId;
        var context = _provider.ImmediateContext;

        try
        {
            while (!_stopRequested)
            {
                Volatile.Write(ref _iterations, Volatile.Read(ref _iterations) + 1);
                _phase = "drain";
                DrainCommands();
                _phase = "pump";
                PumpMessages();

                // Pace on the frame-latency waitable objects (ADR 0002 D3): block — BOUNDED — until the
                // visible swapchains can accept a new frame, then present with sync interval 0. This
                // replaces the per-swapchain Present(1) vsync block, which stalled for seconds at a 1ms
                // system timer. The bounded timeout guarantees the loop can never freeze in the wait.
                _phase = "latency-wait";
                WaitForFrameLatency();

                var presentedAny = false;
                // One time sample per loop iteration so every output in this frame selects against the
                // SAME media time — the basis of cross-monitor content sync (ADR 0002 D3).
                _phase = "clock";
                var mediaTime = Clock?.CurrentMediaTime ?? _clock.Elapsed;
                _phase = "render";
                foreach (var window in _windows)
                    presentedAny |= window.RenderAndPresent(context, mediaTime);

                // Device-removed recovery (Milestone 4) runs inline on this thread — it owns the context
                // and swapchains. After it recreates the device, our cached context is stale, so re-read.
                if (RecoveryHandler is not null && (_injectLossRequested || AnyDeviceLost()))
                {
                    var injected = _injectLossRequested;
                    _injectLossRequested = false;
                    try
                    {
                        RecoveryHandler.Recover(_windows, injected);
                        context = _provider.ImmediateContext;
                    }
                    catch (Exception ex)
                    {
                        // Recovery itself failed (e.g. the GPU is genuinely gone, or a second TDR hit
                        // mid-recreate). Do NOT retry inline — that risks an infinite wedge. Stop the loop
                        // with a distinct, loud error so this is not mistaken for an ordinary render crash;
                        // the finally block runs the normal ordered teardown.
                        _log.Error("Graphics", $"DEVICE-REMOVED RECOVERY FAILED — render loop stopping: {ex}");
                        break;
                    }
                }

                LogPresentRatesIfDue();

                // Present(1, ...) paces the loop at vsync while anything is visible; otherwise
                // idle on the wake event with a short tick so the message pump stays responsive.
                if (!presentedAny)
                    _wake.WaitOne(5);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Graphics", $"Render thread died: {ex}");
        }
        finally
        {
            // Ordered teardown ON the render thread: per-window RTV → swapchain → HWND.
            foreach (var window in _windows)
                window.DisposeCore();
            _windows.Clear();
            PumpMessages(); // drain the WM_DESTROY traffic before the thread exits

            // Never leave an Invoke caller blocked forever.
            while (_commands.TryDequeue(out var command))
            {
                command.Error = new InvalidOperationException("RenderLoop stopped before the command ran.");
                command.Done.Set();
            }
            _threadExited = true; // Invoke waiters racing the drain above see this and bail
        }
    }

    private bool AnyDeviceLost()
    {
        foreach (var window in _windows)
            if (window.DeviceLost)
                return true;
        return false;
    }

    // Bounded so a stalled compositor can never freeze the loop; on timeout we present anyway (a dropped
    // beat, not a wedge). Handles are collected fresh each iteration so a post-recovery swapchain's new
    // waitable object is picked up automatically.
    private const uint FrameLatencyTimeoutMs = 100;
    private readonly IntPtr[] _waitHandles = new IntPtr[16];

    private void WaitForFrameLatency()
    {
        var count = 0;
        foreach (var window in _windows)
        {
            if (!window.Visible || window.DeviceLost)
                continue;
            var handle = window.FrameLatencyWaitable;
            if (handle != IntPtr.Zero && count < _waitHandles.Length)
                _waitHandles[count++] = handle;
        }
        if (count == 0)
            return; // nothing visible to pace against; the idle wait below handles it

        // waitAll=true → pace to the slowest visible swapchain (matches the old Present(1) cadence;
        // cross-monitor content-sync is by frame SELECTION at one clock sample, not present rate).
        // Only the first `count` handles are read; stale entries in the tail are intentionally ignored
        // (do NOT pass _waitHandles.Length here).
        if (Win32.WaitForMultipleObjects((uint)count, _waitHandles, waitAll: true, FrameLatencyTimeoutMs) == Win32.WAIT_TIMEOUT)
            _latencyTimeouts++; // a visible swapchain never signalled ready within the bound — present backed up
    }

    /// <summary>Pause-freeze diagnostic: log per-output present deltas every ~1.5s while anything is visible.
    /// A visible output with a zero delta while others advance is a stalled output — names the freeze.</summary>
    private void LogPresentRatesIfDue()
    {
        var now = _clock.Elapsed;
        if (now < _nextDiagAt)
            return;
        _nextDiagAt = now + TimeSpan.FromMilliseconds(1500);

        var parts = new List<string>();
        var anyVisible = false;
        var anyAdvancing = false;
        var stalled = new List<string>();
        foreach (var window in _windows)
        {
            if (!window.Visible)
                continue;
            anyVisible = true;
            var cur = window.PresentCount;
            var delta = cur - _diagLastPresent.GetValueOrDefault(window.Name);
            _diagLastPresent[window.Name] = cur;
            parts.Add($"{window.Name}=+{delta}({window.RenderPhase})");
            if (delta > 0) anyAdvancing = true;
            else if (!window.DeviceLost) stalled.Add(window.Name);
        }
        if (!anyVisible)
            return;

        var timeouts = _latencyTimeouts;
        _latencyTimeouts = 0;
        _log.Info("Graphics", $"present/1.5s: {string.Join(" ", parts)} latencyTimeouts={timeouts} phase={_phase}");
        // An output presenting nothing while a sibling advances is the multi-output freeze signature.
        if (anyAdvancing && stalled.Count > 0)
            _log.Error("Graphics", $"STALLED OUTPUT(S): {string.Join(", ", stalled)} presented 0 frames while others advanced — freeze suspect.");
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try
            {
                command.Action();
            }
            catch (Exception ex)
            {
                command.Error = ex;
            }
            finally
            {
                command.Done.Set();
            }
        }
    }

    private static void PumpMessages()
    {
        while (Win32.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, Win32.PM_REMOVE))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }
    }
}
