using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
            _windows.Add(CreateOverlay(monitors[i], i + 1));
    }

    public void Hide()
    {
        foreach (var w in _windows)
            w.Close();
        _windows.Clear();
    }

    private Window CreateOverlay(MonitorInfo monitor, int number)
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

        var b = monitor.Bounds;
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
            // MonitorService reports physical pixels with a top-left origin — the same space as
            // PixelPoint/PixelSize — so the overlay covers exactly its monitor on mixed-scale layouts.
            Position = new PixelPoint((int)b.Left, (int)b.Top),
            // Never steal focus: the control panel keeps the keyboard, so Esc there dismisses these too.
            ShowActivated = false,
            Content = card,
        };
        // Unit rule: Position is physical pixels, but Width/Height are DIPs — divide by the screen's scale or
        // a 2x Retina overlay is built twice the size of its monitor.
        var centre = new PixelPoint((int)(b.Left + b.Width / 2), (int)(b.Top + b.Height / 2));
        var scale = w.Screens.ScreenFromPoint(centre)?.Scaling ?? 1.0;
        w.Width = b.Width / scale;
        w.Height = b.Height / scale;
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
}
