using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MultiMon.Control.Shared;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Platform.Mac;

namespace MultiMon.Control.Mac;

/// <summary>
/// Composition root for the Mac control panel — the only place the app constructs the concrete
/// <see cref="PerformanceController"/> (which owns the Metal / VideoToolbox / CoreAudio pipeline);
/// everything downstream sees only <see cref="IPerformanceController"/>.
///
/// <para>Threading (the V0087 rule, MAC_SETUP §7). The controller creates and destroys its output windows
/// by marshalling onto the AppKit main queue, which only drains while the run loop is pumping — so BOTH
/// its construction and its disposal run on a background thread that the UI thread <c>await</c>s rather
/// than blocks on. Blocking the UI thread across either would starve the main queue and turn a 5 s bounded
/// wait into the V0087 deadlock. Construction therefore happens after the loop has started (posted, not
/// inline), and shutdown cancels the first close, tears down asynchronously, then exits for real.</para>
/// </summary>
public partial class App : Application
{
    /// <summary>Teardown ceiling at exit. A verified-clean teardown never reaches it (the harness proves
    /// the same path over 50 cycles); it exists so a wedge logs and exits instead of hanging the app.</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(10);

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private PerformanceController? _controller;
    private FileLog? _log;
    private MonitorService? _monitorService;
    private int _exiting;               // 0 until an exit has been started; makes RequestExitAsync idempotent
    private bool _teardownComplete;     // true once the pipeline is down and Avalonia may stop for real

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            // No window exists until the pipeline is built, so the app must not exit for want of one.
            // It stays on OnExplicitShutdown for the whole session: the window close is intercepted and
            // routed through RequestExitAsync so the pipeline is torn down BEFORE the run loop stops.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnShutdownRequested;
            // Posted, not called: the pipeline build needs the AppKit run loop already pumping.
            Dispatcher.UIThread.Post(() => _ = StartAsync(desktop));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // File sink first: a Finder launch has no console, so a normal run would drop every log line.
        var log = _log = new FileLog();
        RegisterCrashHandlers(log);
        log.Info("App", $"MultiMon (macOS) starting (log: {log.FilePath ?? "console only"}).");
        LogEnvironment(log);

        _monitorService = new MonitorService(log);
        var monitors = _monitorService.GetMonitors();
        log.Info("App", $"Monitors: {monitors.Count} — {string.Join(", ", monitors.Select(m => m.ToString()))}");

        try
        {
            // Off the UI thread and awaited: the run loop keeps draining the main queue the output-window
            // creation marshals onto, so the bounded wait inside the controller can actually complete.
            _controller = await Task.Run(() => new PerformanceController(monitors, log));
        }
        catch (Exception ex)
        {
            log.Error("App", $"Startup failed: {ex}");
            ShowStartupFailure(desktop, ex, log.FilePath);
            return;
        }

        var vm = new MainViewModel(_controller, log, new AvaloniaUiDispatcher());
        var window = new MainWindow(vm, log);
        desktop.MainWindow = window;
        // Closing the panel means "quit", but the pipeline must come down while the run loop is still
        // pumping — so cancel this close and route it through the ordered exit, which closes for real.
        window.Closing += (_, e) =>
        {
            if (_teardownComplete)
                return;
            e.Cancel = true;
            _ = RequestExitAsync();
        };
        window.Show();

        // Dev switch (documented in docs/MAC_SETUP.md §5): --autoperform=<clip> loads the clip on monitor 1,
        // performs briefly, stops and exits 0. It drives the SAME view-model the user clicks
        // (LESSON-TEST-004), so it gates the real path rather than a lookalike.
        var flag = desktop.Args?.FirstOrDefault(a => a.StartsWith(AutoPerform.Flag, StringComparison.Ordinal));
        if (flag is not null)
            _ = AutoPerform.RunAsync(vm, log, flag[AutoPerform.Flag.Length..], this);
    }

    private void ShowStartupFailure(IClassicDesktopStyleApplicationLifetime desktop, Exception error, string? logPath)
    {
        var window = new Window
        {
            Title = "MultiMon",
            Width = 620,
            Height = 260,
            Content = new TextBlock
            {
                Margin = new Thickness(24),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text = $"MultiMon could not start its graphics pipeline.\n\n{error.Message}\n\n" +
                       $"Details are in the log:\n{logPath ?? "(console only)"}",
            },
        };
        desktop.MainWindow = window;
        window.Closing += (_, e) =>
        {
            if (_teardownComplete)
                return;
            e.Cancel = true;
            _ = RequestExitAsync(1);
        };
        window.Show();
    }

    /// <summary>An OS-initiated quit (Cmd+Q, Log Out). Avalonia honours Cancel here, so hold the app open
    /// and route the quit through the ordered exit — otherwise the run loop would stop with the pipeline
    /// still up and the render thread's window disposal would wait forever on a main queue that no longer
    /// drains.</summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_teardownComplete)
            return;
        e.Cancel = true;
        _ = RequestExitAsync();
    }

    /// <summary>
    /// The app's ONE exit path, idempotent and safe to call from anywhere on the UI thread: tear the
    /// pipeline down first, stop Avalonia second. The order is not cosmetic — <c>desktop.Shutdown()</c>
    /// does NOT honour a cancelled <c>ShutdownRequested</c>, so calling it first ends the run loop, and the
    /// controller's disposal (which joins the render thread, where the output windows are destroyed on the
    /// main queue) would then block forever. Teardown itself runs on a background thread that the UI
    /// thread <c>await</c>s rather than blocks on, so the main queue keeps draining throughout.
    /// </summary>
    public async Task RequestExitAsync(int exitCode = 0)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
            return; // an exit is already in flight

        var controller = Interlocked.Exchange(ref _controller, null);
        if (controller is not null)
        {
            _log?.Info("App", "Teardown: disposing the pipeline (off the UI thread; the run loop keeps pumping).");
            var started = System.Diagnostics.Stopwatch.StartNew();
            var teardown = Task.Run(controller.Dispose);
            if (await Task.WhenAny(teardown, Task.Delay(TeardownTimeout)) != teardown)
            {
                _log?.Error("App", $"Teardown did not complete within {TeardownTimeout.TotalSeconds:0}s; forcing exit.");
                _log?.Dispose();
                Environment.Exit(exitCode);
            }
            await teardown; // observe a teardown fault in the log rather than silently
            // The Graphics lines just above carry the resource evidence ("Metal device released...").
            _log?.Info("App", $"teardown complete: live=0 in {started.ElapsedMilliseconds}ms.");
        }

        _monitorService?.Dispose();
        _monitorService = null;
        _log?.Info("App", "MultiMon exited cleanly.");
        _log?.Dispose();
        _log = null;

        _teardownComplete = true;
        _desktop?.Shutdown(exitCode);
    }

    /// <summary>Captures every route an unhandled exception can take out of the app into the log. We log a
    /// full stack and let the process terminate — unknown faults are never swallowed.</summary>
    private static void RegisterCrashHandlers(ILog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            log.Error("CRASH", $"Unhandled exception (terminating={ex.IsTerminating}):\n{Describe(ex.ExceptionObject as Exception)}");

        TaskScheduler.UnobservedTaskException += (_, ex) =>
            log.Error("CRASH", $"Unobserved task exception:\n{Describe(ex.Exception)}");
    }

    private static void LogEnvironment(ILog log)
    {
        log.Info("Env", $"OS={Environment.OSVersion} machine={Environment.MachineName} cores={Environment.ProcessorCount}");
        log.Info("Env", $"runtime={RuntimeInformation.FrameworkDescription} procArch={RuntimeInformation.ProcessArchitecture} " +
            $"appDir={AppContext.BaseDirectory}");
    }

    private static string Describe(Exception? ex)
    {
        if (ex is null) return "(no exception object)";
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
            sb.Append("  ").Append(e.GetType().FullName).Append(": ").AppendLine(e.Message).AppendLine(e.StackTrace);
        return sb.ToString();
    }
}
