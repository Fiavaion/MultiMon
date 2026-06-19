using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MultiMon.Core.Models;

namespace MultiMon.Control.Views;

/// <summary>
/// Windows-11-style "Identify": a borderless number overlay on each physical monitor so the performer can
/// tell which screen is "Monitor 1" before assigning videos. Pure WPF — no video, no D3D11; it never touches
/// the output swapchains or the render thread. Toggled from the control panel and auto-hidden when a show
/// starts. Click any overlay to dismiss.
/// </summary>
internal sealed class IdentifyOverlays
{
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

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
        var numberText = new TextBlock
        {
            Text = number.ToString(),
            FontSize = 240,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0xE2, 0xE6)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = $"{monitor.DisplayName}   ·   {monitor.Resolution}",
            FontSize = 22,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xDC)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -10, 0, 0),
        };
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x14, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0xE2, 0xE6)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(48, 28, 48, 36),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel { Children = { numberText, label } },
        };

        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            AllowsTransparency = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            // Faint scrim so the number reads clearly while the desktop stays visible behind it.
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0x06, 0x08, 0x0C)),
            Content = new Grid { Children = { card } },
        };

        var b = monitor.Bounds;
        w.SourceInitialized += (_, _) =>
        {
            // Position by PHYSICAL pixel bounds (the app is per-monitor-V2 DPI-aware) — covering the exact
            // monitor without WPF DIP conversion. NOACTIVATE so it doesn't steal focus from the panel.
            var hwnd = new WindowInteropHelper(w).Handle;
            SetWindowPos(hwnd, 0, (int)b.Left, (int)b.Top, (int)b.Width, (int)b.Height, SWP_NOACTIVATE | SWP_NOZORDER);
        };
        w.MouseLeftButtonDown += (_, _) => Hide();
        w.Show();
        return w;
    }
}
