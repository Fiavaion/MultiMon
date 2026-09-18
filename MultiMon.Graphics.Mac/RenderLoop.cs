using System.Collections.Concurrent;
using System.Diagnostics;
using Foundation;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// The single render/present thread — created once, alive for the whole session. It exclusively owns
/// every command buffer, every render encoder and every present (the Metal twin of
/// <c>MultiMon.Graphics.RenderLoop</c>). Other threads interact only via <see cref="Invoke"/>, which
/// marshals a command onto this thread and blocks until it ran. The render thread never touches AppKit
/// and never waits on the main thread; the main thread never waits on the render thread — both
/// <see cref="Invoke"/> and <see cref="Stop"/> throw when called on the AppKit main thread.
///
/// Stop() is the ONLY teardown, runs off the main thread, and is strictly ordered: signal stop → loop
/// exits → thread joined → each window drains its in-flight work, closes on the main thread and releases its
/// Metal objects. The caller releases the device after Stop returns (graphics-core disposal-order rule).
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
    private readonly List<OutputWindow> _windows = new(); // render-thread-owned while running; stopping thread after Join

    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _threadExited;
    private int _renderThreadId = -1;

    // Wedge diagnostics: a heartbeat the harness samples to tell a stalled render thread from one that is
    // merely not presenting.
    private long _iterations;
    private volatile string _phase = "init";
    public long Iterations => Volatile.Read(ref _iterations);
    public string Phase => _phase;

    private readonly Dictionary<string, long> _diagLastPresent = new();
    private TimeSpan _nextDiagAt = TimeSpan.Zero;

    public RenderLoop(GraphicsDeviceProvider provider, ILog log)
    {
        _provider = provider;
        _log = log;
    }

    /// <summary>The playback timeline the render thread reads each frame to drive frame selection. Null
    /// (test-pattern runs) = the loop's own stopwatch, so the animated pattern still moves.</summary>
    public MasterClock? Clock { get; set; }

    public bool IsRunning => _thread is not null;

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
    /// Stops the loop, joins the thread, then disposes every output window (in-flight drain → main-thread
    /// close → Metal objects). Never call from the render thread or the main thread: the window close is
    /// marshalled onto the main thread, which must be free to pump it.
    /// </summary>
    public void Stop()
    {
        var thread = _thread;
        if (thread is null)
            return;
        ThrowIfRenderThread(nameof(Stop));
        if (NSThread.IsMain)
            throw new InvalidOperationException("RenderLoop.Stop must not be called on the main thread (teardown is off the UI thread).");

        _stopRequested = true;
        _wake.Set();
        thread.Join();
        _thread = null;

        foreach (var window in _windows)
            window.DisposeCore();
        _windows.Clear();
        _log.Info("Graphics", "Render loop stopped and joined; all output windows disposed.");
    }

    /// <summary>Creates a persistent output window (its NSWindow on the main thread) and registers it with
    /// the render thread. Caller: controller/harness thread — never the render thread.</summary>
    public OutputWindow CreateOutputWindow(string name, MonitorRect initialBounds)
    {
        ThrowIfRenderThread(nameof(CreateOutputWindow));
        if (_thread is null || _threadExited)
            throw new InvalidOperationException("RenderLoop is not running — cannot create an output window.");
        var window = new OutputWindow(_provider, this, _log, name, initialBounds);
        if (!Invoke(() => _windows.Add(window)))
        {
            window.DisposeCore();
            throw new InvalidOperationException("RenderLoop stopped while creating an output window.");
        }
        return window;
    }

    internal void ThrowIfRenderThread(string operation)
    {
        if (Environment.CurrentManagedThreadId == _renderThreadId)
            throw new InvalidOperationException($"{operation} must not be called from the render thread.");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the render thread and blocks until it completed; returns true when
    /// it ran. Returns false — logged once, never thrown — when the render thread is not alive, so a caller
    /// mid-teardown still reaches Stop/Release. A command that DID run and threw surfaces as an exception.
    /// Never call from the main thread: it must not wait on the render thread (the V0087 deadlock rule).
    /// </summary>
    public bool Invoke(Action action)
    {
        if (Environment.CurrentManagedThreadId == _renderThreadId)
        {
            action();
            return true;
        }
        if (NSThread.IsMain)
            throw new InvalidOperationException("RenderLoop.Invoke must not be called on the main thread (the main thread never waits on the render thread).");
        if (_thread is null || _threadExited)
            return LogDeadThreadOnce();

        var command = new Command { Action = action };
        _commands.Enqueue(command);
        _wake.Set();
        while (!command.Done.Wait(100))
        {
            if (_threadExited)
                return LogDeadThreadOnce();
        }
        if (command.Error is RenderLoopStoppedException)
            return LogDeadThreadOnce();
        if (command.Error is not null)
            throw new InvalidOperationException($"Render-thread command failed: {command.Error.Message}", command.Error);
        return true;
    }

    private sealed class RenderLoopStoppedException : InvalidOperationException
    {
        public RenderLoopStoppedException() : base("RenderLoop stopped before the command ran.") { }
    }

    private int _deadThreadLogged;

    private bool LogDeadThreadOnce()
    {
        if (Interlocked.Exchange(ref _deadThreadLogged, 1) == 0)
            _log.Error("Graphics", "RenderLoop.Invoke with no live render thread (not started, stopped, or died) — command skipped; further skips are not logged.");
        return false;
    }

    private void ThreadProc()
    {
        _renderThreadId = Environment.CurrentManagedThreadId;
        var queue = _provider.CommandQueue;

        try
        {
            while (!_stopRequested)
            {
                // One autorelease pool per iteration (the @autoreleasepool-per-frame Metal idiom): nextDrawable,
                // commandBuffer, drawable.texture and the encoders are returned autoreleased, and a thread that
                // never drains its pool retains every one of them until it exits — measured as ~7KB/frame native
                // growth (2MB/frame under shader validation) before this pool existed.
                using var pool = new NSAutoreleasePool();
                Volatile.Write(ref _iterations, Volatile.Read(ref _iterations) + 1);
                _phase = "drain";
                DrainCommands();

                // One time sample per iteration so every output selects against the SAME media time — the
                // basis of cross-monitor content sync. Pacing is the layer's vsync-locked nextDrawable.
                _phase = "clock";
                var mediaTime = Clock?.CurrentMediaTime ?? _clock.Elapsed;
                _phase = "render";
                var presentedAny = false;
                foreach (var window in _windows)
                    presentedAny |= window.RenderAndPresent(queue, mediaTime);

                LogPresentRatesIfDue();

                // nextDrawable paces the loop while anything is visible; otherwise idle on the wake event
                // with a short tick so commands are still drained promptly.
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
            // Never leave an Invoke caller blocked forever.
            while (_commands.TryDequeue(out var command))
            {
                command.Error = new RenderLoopStoppedException();
                command.Done.Set();
            }
            _threadExited = true;
        }
    }

    /// <summary>Freeze diagnostic: log per-output completed-frame deltas every ~1.5s while anything is visible.</summary>
    private void LogPresentRatesIfDue()
    {
        var now = _clock.Elapsed;
        if (now < _nextDiagAt)
            return;
        _nextDiagAt = now + TimeSpan.FromMilliseconds(1500);

        var parts = new List<string>();
        var anyAdvancing = false;
        var stalled = new List<string>();
        foreach (var window in _windows)
        {
            if (!window.Visible)
                continue;
            var cur = window.PresentCount;
            var delta = cur - _diagLastPresent.GetValueOrDefault(window.Name);
            _diagLastPresent[window.Name] = cur;
            parts.Add($"{window.Name}=+{delta}({window.RenderPhase})");
            if (delta > 0) anyAdvancing = true;
            else stalled.Add(window.Name);
        }
        if (parts.Count == 0)
            return;

        _log.Info("Graphics", $"present/1.5s: {string.Join(" ", parts)} phase={_phase} {_provider.Tracker}");
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
}
