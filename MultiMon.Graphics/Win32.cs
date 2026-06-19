using System.Runtime.InteropServices;

namespace MultiMon.Graphics;

/// <summary>
/// Minimal Win32 interop for the bare output windows. Internal to MultiMon.Graphics — the only
/// consumers are <see cref="OutputWindow"/> (create/show/hide/destroy) and <see cref="RenderLoop"/>
/// (message pump). All of these calls happen on the render thread, which owns the HWNDs.
/// </summary>
internal static class Win32
{
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_TOPMOST = 0x00000008;

    public const int SW_HIDE = 0;

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // Z-order insert-after handles for SetWindowPos (passed as the insertAfter HWND).
    public static readonly IntPtr HWND_TOPMOST = new(-1);   // above all non-topmost windows

    public const uint PM_REMOVE = 0x0001;
    public const uint WM_CLOSE = 0x0010;

    public const uint WAIT_TIMEOUT = 0x00000102;
    public const uint WAIT_FAILED = 0xFFFFFFFF;

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    // Diagnostics: confirm where a window ACTUALLY landed and whether it is visible (the on-screen
    // ground truth the present-count gate can't see).
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    // Frame-latency waitable swapchain pacing: the render thread blocks here (bounded) until the
    // swapchains can accept a new frame, replacing the indefinite block that Present(1) suffered when
    // the system timer is at 1ms (ADR 0002 D3). Bounded timeout → the present path can never freeze.
    [DllImport("kernel32.dll")]
    public static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
}
