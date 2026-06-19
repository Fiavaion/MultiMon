using System;
using System.Runtime.InteropServices;

namespace MultiMon.Control.Theme;

/// <summary>
/// Paints the Win32 non-client title bar dark to match the "Signal" theme (the WPF theme only reaches the
/// client area; the caption is OS-drawn). Pure cosmetic — call once from a window's SourceInitialized.
/// </summary>
internal static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on Windows 10 2004+/Windows 11; 19 on earlier 1809–1903 builds.
    private const int AttrCurrent = 20;
    private const int AttrLegacy = 19;

    public static void Apply(IntPtr hwnd)
    {
        int enabled = 1;
        if (DwmSetWindowAttribute(hwnd, AttrCurrent, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, AttrLegacy, ref enabled, sizeof(int));
    }
}
