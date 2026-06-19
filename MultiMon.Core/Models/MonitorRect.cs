namespace MultiMon.Core.Models;

/// <summary>
/// Framework-agnostic rectangle. Replaces the WPF <c>System.Windows.Rect</c> that the old
/// <c>MonitorInfo</c> used, so Core stays UI-free. Values are physical pixels in the virtual
/// desktop coordinate space (the space DXGI/Win32 position swapchain windows in).
/// </summary>
public readonly record struct MonitorRect(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public override string ToString() => $"{Width}x{Height}@({X},{Y})";
}
