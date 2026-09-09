using AppKit;
using Avalonia;

namespace MultiMon.Control.Mac;

internal static class Program
{
    /// <summary>
    /// Avalonia owns the NSApplication run loop, but the whole output pipeline is Microsoft.macOS
    /// (AppKit / Metal / CoreAudio) and its bindings refuse every call until <see cref="NSApplication.Init"/>
    /// has recorded which thread is the main one — without it even a correctly marshalled
    /// <c>DispatchQueue.MainQueue</c> block throws <c>AppKitThreadAccessException</c>. So Init runs FIRST,
    /// on the main thread, before Avalonia starts; Avalonia's run loop then drains the main queue that
    /// <c>MultiMon.Graphics.Mac.MainThread.Invoke</c> posts window operations onto. Verified by spike:
    /// both windows live, the Metal output presents at full rate from its own thread while Avalonia keeps
    /// handling input.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        NSApplication.Init();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
