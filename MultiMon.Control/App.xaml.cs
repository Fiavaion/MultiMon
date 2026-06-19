using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using MultiMon.Control.ViewModels;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Platform;

namespace MultiMon.Control;

/// <summary>
/// DI bootstrap for the thin, video-free control panel. This is the COMPOSITION ROOT — the only place
/// the control app constructs the concrete <see cref="PerformanceController"/> (which owns the D3D11/
/// decode pipeline); everything downstream sees it only as <see cref="IPerformanceController"/> and
/// issues commands. All native lifecycle lives on the render/decode threads, and teardown runs OFF the
/// UI thread at exit (the V0087 deadlock guard).
/// </summary>
public partial class App : Application
{
    private IServiceProvider _services = null!;
    private PerformanceController? _controller;
    private FileLog? _log;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // File sink first: the windowed .exe has no console, so a normal launch would drop every log line.
        // FileLog also echoes to the console for redirected/diagnostic launches.
        var log = _log = new FileLog();

        // Crash handlers next, BEFORE any startup work, so a fault below is captured. On a remote PC the
        // log is the only window into what went wrong.
        RegisterCrashHandlers(log);

        log.Info("App", $"MultiMon starting (log: {log.FilePath ?? "console only"}).");
        LogEnvironment(log);
        GpuCapabilityService.LogAdapters(log);   // also runs detection (gates the HAP enhancement)

        var monitorService = new MonitorService(log);
        var monitors = monitorService.GetMonitors();

        // Composition root: build the persistent pipeline once (device + render loop + one output per
        // monitor). Downstream code depends only on the IPerformanceController abstraction.
        _controller = new PerformanceController(monitors, log);

        var services = new ServiceCollection();
        services.AddSingleton<ILog>(log);
        services.AddSingleton<IPerformanceController>(_controller);
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Dispose the pipeline OFF the UI thread (the V0087 rule): stop sources/audio → join the render
        // thread (windows + swapchains die there) → release the device. Joined so the device is cleanly
        // released before the process exits.
        if (_controller is not null)
        {
            var teardown = new Thread(_controller.Dispose) { Name = "MultiMon.Teardown", IsBackground = true };
            teardown.Start();
            if (!teardown.Join(TimeSpan.FromSeconds(10)))
            {
                // Teardown wedged — the V0087 failure mode, now relocated to exit. Don't freeze the process:
                // log and force-terminate rather than hang the window forever. Verified-clean teardown never
                // reaches this branch (50-cycle harness + manual), so the 10s ceiling is pure margin.
                _log?.Error("App", "Teardown did not complete within 10s; forcing exit.");
                _log?.Dispose();
                Environment.Exit(0);
            }
            _controller = null;
        }

        // Flush + close the log last, so teardown lines above are persisted.
        _log?.Info("App", "MultiMon exited cleanly.");
        _log?.Dispose();
        _log = null;
        base.OnExit(e);
    }

    /// <summary>Captures every route an unhandled exception can take out of the app (UI dispatcher, any
    /// thread via the AppDomain, faulted tasks) into the log. We log a full stack and let the process
    /// terminate — unknown faults are never swallowed (that would mask corruption); the goal is a
    /// diagnosable record on machines where no debugger is attached.</summary>
    private void RegisterCrashHandlers(ILog log)
    {
        DispatcherUnhandledException += (_, ex) =>
            log.Error("CRASH", $"Unhandled UI-thread exception:\n{Describe(ex.Exception)}");

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            log.Error("CRASH", $"Unhandled exception (terminating={ex.IsTerminating}):\n{Describe(ex.ExceptionObject as Exception)}");

        TaskScheduler.UnobservedTaskException += (_, ex) =>
            log.Error("CRASH", $"Unobserved task exception:\n{Describe(ex.Exception)}");
    }

    private static void LogEnvironment(ILog log)
    {
        log.Info("Env", $"OS={Environment.OSVersion} 64bitOS={Environment.Is64BitOperatingSystem} " +
            $"machine={Environment.MachineName} cores={Environment.ProcessorCount}");
        log.Info("Env", $"runtime={RuntimeInformation.FrameworkDescription} procArch={RuntimeInformation.ProcessArchitecture} " +
            $"appDir={AppContext.BaseDirectory}");
    }

    /// <summary>Full type + message + stack for an exception and its inner chain.</summary>
    private static string Describe(Exception? ex)
    {
        if (ex is null) return "(no exception object)";
        var sb = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
            sb.Append("  ").Append(e.GetType().FullName).Append(": ").AppendLine(e.Message).AppendLine(e.StackTrace);
        return sb.ToString();
    }
}
