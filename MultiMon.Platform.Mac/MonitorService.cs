using System.Runtime.InteropServices;
using AppKit;
using Foundation;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Platform.Mac;

/// <summary>
/// Detects display monitors via AppKit (<see cref="NSScreen"/>) — the macOS twin of
/// <c>MultiMon.Platform.MonitorService</c>.
/// <para>
/// <b>Coordinate convention.</b> AppKit reports screen frames in points, in one global desktop space whose
/// origin is the <i>bottom-left</i> of the primary screen with Y growing <i>upward</i>; every screen's
/// origin is measured in that single space regardless of its own DPI. The Core contract
/// (<see cref="MonitorRect"/>) is physical pixels in a virtual-desktop space whose origin is the
/// <i>top-left</i> of the primary screen with Y growing <i>downward</i>, which is what window placement and
/// <c>ShowPlanner</c> expect. The conversion rule: <b>Origins: AppKit global points × the primary screen's
/// backing scale, Y flipped about the primary's top edge. Sizes: each screen's own points × its own backing
/// scale.</b> A Retina screen therefore reports its native pixel size (exactly as the Win32 side reports
/// physical pixels per monitor) and the primary screen is always at (0,0).
/// </para>
/// </summary>
public sealed class MonitorService : IMonitorService, IDisposable
{
    private readonly ILog? _log;
    private NSObject? _screenParametersObserver;
    private List<MonitorInfo> _monitors = new();

    public event EventHandler? MonitorsChanged;

    public MonitorService(ILog? log = null)
    {
        _log = log;
        _screenParametersObserver = NSApplication.Notifications.ObserveDidChangeScreenParameters(
            (_, _) => RefreshMonitors());
        RefreshMonitors();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => _monitors.AsReadOnly();

    public void RefreshMonitors()
    {
        var screens = NSScreen.Screens;
        var found = new List<MonitorInfo>(screens.Length);

        if (screens.Length == 0)
        {
            _log?.Error("MonitorService", "NSScreen.Screens returned no displays");
            _monitors = found;
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Screens[0] is the screen carrying the menu bar — the primary. Its backing scale converts every
        // screen's global-space origin, and its top edge in AppKit space is the Y-flip reference.
        var primary = screens[0];
        var primaryScale = (double)primary.BackingScaleFactor;
        var primaryTopPoints = (double)primary.Frame.Y + (double)primary.Frame.Height;

        foreach (var screen in screens)
        {
            var displayId = GetDisplayId(screen);
            var scale = (double)screen.BackingScaleFactor;
            var bounds = ToPixelRect(screen.Frame, primaryTopPoints, primaryScale, scale);
            var isPrimary = ReferenceEquals(screen, primary);

            found.Add(new MonitorInfo
            {
                DeviceId = displayId.ToString(),
                DisplayName = screen.LocalizedName + (isPrimary ? " (Primary)" : string.Empty),
                Bounds = bounds,
                WorkArea = ToPixelRect(screen.VisibleFrame, primaryTopPoints, primaryScale, scale),
                Resolution = $"{(int)bounds.Width}x{(int)bounds.Height}",
                RefreshRate = GetRefreshRate(displayId),
                IsPrimary = isPrimary
            });
        }

        // Primary first, then left-to-right, top-to-bottom — same ordering contract as the Win32 service.
        found.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            var x = a.Bounds.Left.CompareTo(b.Bounds.Left);
            return x != 0 ? x : a.Bounds.Top.CompareTo(b.Bounds.Top);
        });

        _monitors = found;
        _log?.Info("MonitorService", $"Detected {_monitors.Count} monitor(s)");
        foreach (var m in _monitors)
            _log?.Info("MonitorService", $"  {m.DisplayName}: {m.Resolution}@{m.RefreshRate:0}Hz " +
                $"bounds=({m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height}) device={m.DeviceId}");
        MonitorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// AppKit points (bottom-left origin, Y up) → global-desktop pixels (top-left origin, Y down).
    /// The origin is in the shared global space, so it scales by <paramref name="primaryScale"/>; the size
    /// is this screen's own, so it scales by <paramref name="ownScale"/>.
    /// </summary>
    private static MonitorRect ToPixelRect(CoreGraphics.CGRect frame, double primaryTopPoints,
        double primaryScale, double ownScale)
    {
        var topPoints = primaryTopPoints - ((double)frame.Y + (double)frame.Height);
        return new MonitorRect(
            (double)frame.X * primaryScale,
            topPoints * primaryScale,
            (double)frame.Width * ownScale,
            (double)frame.Height * ownScale);
    }

    /// <summary>The screen's CGDirectDisplayID — the opaque key <c>MonitorInfo.DeviceId</c> carries.</summary>
    private uint GetDisplayId(NSScreen screen)
    {
        if (screen.DeviceDescription["NSScreenNumber"] is NSNumber number) return number.UInt32Value;
        _log?.Error("MonitorService", $"Screen '{screen.LocalizedName}' has no NSScreenNumber");
        return 0;
    }

    private double GetRefreshRate(uint displayId)
    {
        // NSScreen.MaximumFramesPerSecond is bound but reports the panel's maximum, whereas RefreshRate drives
        // per-monitor present cadence and must be the CURRENT mode's rate (a 120 Hz ProMotion panel driven at
        // 60 must report 60). Only CGDisplayMode exposes the current mode, hence the P/Invokes below.
        var mode = CGDisplayCopyDisplayMode(displayId);
        if (mode == IntPtr.Zero)
        {
            _log?.Info("MonitorService", $"No display mode for display {displayId}; assuming 60Hz");
            return 60.0;
        }

        double hz;
        try { hz = CGDisplayModeGetRefreshRate(mode); }
        finally { CGDisplayModeRelease(mode); }

        // Built-in and many DisplayPort panels report 0 — CoreGraphics has no rate for them.
        if (hz <= 0.0)
        {
            _log?.Info("MonitorService", $"Display {displayId} reports no refresh rate; assuming 60Hz");
            return 60.0;
        }
        return hz;
    }

    public void Dispose()
    {
        _screenParametersObserver?.Dispose();
        _screenParametersObserver = null;
    }

    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphicsLib)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphicsLib)]
    private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);

    [DllImport(CoreGraphicsLib)]
    private static extern void CGDisplayModeRelease(IntPtr mode);
}
