using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using MultiMon.Core.Models;

namespace MultiMon.Control.Mac;

/// <summary>
/// "Identify": a borderless number overlay on each physical monitor so the performer can tell which screen
/// is "Monitor 1" before assigning videos. Pure Avalonia — no Metal, no output window; it never touches the
/// render loop or a CAMetalLayer. Toggled from the control panel, auto-hidden when a show starts. Click any
/// overlay, or press Esc (in the panel, which keeps focus, or on an overlay), to dismiss. UI-thread only,
/// like every other window here.
/// </summary>
internal sealed class IdentifyOverlays
{
    private readonly List<Window> _windows = new();

    public bool Active => _windows.Count > 0;

    /// <summary>Show the overlays if hidden, hide them if shown.</summary>
    public void Toggle(IReadOnlyList<MonitorInfo> monitors)
    {
        if (Active) Hide();
        else Show(monitors);
    }

    public void Show(IReadOnlyList<MonitorInfo> monitors)
    {
        Hide();
        for (var i = 0; i < monitors.Count; i++)
            _windows.Add(CreateOverlay(monitors[i], i + 1, monitors));
    }

    public void Hide()
    {
        foreach (var w in _windows)
            w.Close();
        _windows.Clear();
    }

    private Window CreateOverlay(MonitorInfo monitor, int number, IReadOnlyList<MonitorInfo> monitors)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x14, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0xE2, 0xE6)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(48, 28, 48, 36),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = number.ToString(),
                        FontSize = 240,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0xE2, 0xE6)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = $"{monitor.DisplayName}   ·   {monitor.Resolution}",
                        FontSize = 22,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xDC)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, -10, 0, 0),
                    },
                },
            },
        };

        var w = new Window
        {
            WindowDecorations = WindowDecorations.None,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            // Faint scrim so the number reads clearly while the desktop stays visible behind it.
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x06, 0x08, 0x0C)),
            WindowStartupLocation = WindowStartupLocation.Manual,
            // Never steal focus: the control panel keeps the keyboard, so Esc there dismisses these too.
            ShowActivated = false,
            Content = card,
        };
        // Coordinate rule (two-display pass, 2026-09-14): MonitorService reports physical pixels — origins in
        // AppKit points × the PRIMARY screen's backing scale (ADR 0005). Avalonia's Screens on macOS are in
        // AppKit points (scaling reported as 1), so a Retina laptop + 1080p external puts the external at
        // x=4112 for us but x=2056 for Avalonia; positioning with our pixels lands the overlay off-screen and
        // AppKit clamps it back onto the laptop. Convert our origin to points via the primary pair, then let
        // the Avalonia screen containing that point supply position and size in its own units.
        var pixelOrigin = new PixelPoint((int)monitor.Bounds.Left, (int)monitor.Bounds.Top);
        var screen = w.Screens.ScreenFromPoint(ToAvaloniaPoint(pixelOrigin, monitors, w.Screens.All))
                     ?? w.Screens.Primary
                     ?? throw new InvalidOperationException("No screens reported by Avalonia.");
        w.Position = screen.Bounds.Position;
        w.Width = screen.Bounds.Width / screen.Scaling;
        w.Height = screen.Bounds.Height / screen.Scaling;
        w.PointerPressed += (_, _) => Hide();
        w.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Space)
            {
                Hide();
                e.Handled = true;
            }
        };
        w.Show();
        return w;
    }

    /// <summary>Physical-pixel origin (MonitorService space) → AppKit-point origin (Avalonia screen space).
    /// Both spaces share the primary's top-left as (0,0); the ratio between them is the primary's backing scale,
    /// read off the primary pair rather than assumed.</summary>
    private static PixelPoint ToAvaloniaPoint(PixelPoint pixels, IReadOnlyList<MonitorInfo> monitors, IReadOnlyList<Screen> screens)
    {
        var ourPrimary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        var avPrimary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens[0];
        var scale = ourPrimary.Bounds.Width / (avPrimary.Bounds.Width * avPrimary.Scaling);
        return new PixelPoint((int)Math.Round(pixels.X / scale), (int)Math.Round(pixels.Y / scale));
    }
}
