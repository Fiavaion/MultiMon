using System.Runtime.InteropServices;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Platform;

/// <summary>
/// Detects display monitors via the Win32 API (EnumDisplayMonitors + DEVMODE). Salvaged from the
/// old app, de-WPF'd: bounds are <see cref="MonitorRect"/> and there is no UI dependency.
/// </summary>
public class MonitorService : IMonitorService
{
    private readonly ILog? _log;
    private List<MonitorInfo> _monitors = new();

    public event EventHandler? MonitorsChanged;

    public MonitorService(ILog? log = null)
    {
        _log = log;
        RefreshMonitors();
    }

    public IReadOnlyList<MonitorInfo> GetMonitors() => _monitors.AsReadOnly();

    public void RefreshMonitors()
    {
        var found = new List<MonitorInfo>();
        var index = 0;

        var ok = NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi)) return true;

                var isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                var deviceName = mi.szDevice;

                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                double refreshRate = 60.0;
                int width = 0, height = 0;
                if (NativeMethods.EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
                {
                    refreshRate = dm.dmDisplayFrequency;
                    width = dm.dmPelsWidth;
                    height = dm.dmPelsHeight;
                }
                if (width == 0 || height == 0)
                {
                    width = mi.rcMonitor.right - mi.rcMonitor.left;
                    height = mi.rcMonitor.bottom - mi.rcMonitor.top;
                }

                index++;
                found.Add(new MonitorInfo
                {
                    DeviceId = deviceName,
                    DisplayName = $"Monitor {index}",
                    WorkArea = ToRect(mi.rcWork),
                    Bounds = ToRect(mi.rcMonitor),
                    Resolution = $"{width}x{height}",
                    RefreshRate = refreshRate,
                    IsPrimary = isPrimary
                });
                return true;
            }, IntPtr.Zero);

        if (!ok) _log?.Error("MonitorService", "EnumDisplayMonitors failed");

        // Primary first, then left-to-right, top-to-bottom.
        found.Sort((a, b) =>
        {
            if (a.IsPrimary != b.IsPrimary) return a.IsPrimary ? -1 : 1;
            var x = a.WorkArea.Left.CompareTo(b.WorkArea.Left);
            return x != 0 ? x : a.WorkArea.Top.CompareTo(b.WorkArea.Top);
        });
        for (int i = 0; i < found.Count; i++)
            found[i].DisplayName = $"Monitor {i + 1}{(found[i].IsPrimary ? " (Primary)" : "")}";

        _monitors = found;
        _log?.Info("MonitorService", $"Detected {_monitors.Count} monitor(s)");
        foreach (var m in _monitors)
            _log?.Info("MonitorService", $"  {m.DisplayName}: {m.Resolution}@{m.RefreshRate:0}Hz " +
                $"bounds=({m.Bounds.Left},{m.Bounds.Top} {m.Bounds.Width}x{m.Bounds.Height}) device={m.DeviceId}");
        MonitorsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static MonitorRect ToRect(RECT r)
        => new(r.left, r.top, r.right - r.left, r.bottom - r.top);

    #region Native

    private const int MONITORINFOF_PRIMARY = 0x00000001;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CCHDEVICENAME = 32;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX mi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    }

    #endregion
}
