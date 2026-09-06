using AppKit;
using CoreGraphics;
using MultiMon.Core.Models;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// The inverse of <c>MultiMon.Platform.Mac.MonitorService</c>'s coordinate rule: Core pixel rects (top-left
/// origin at the primary screen, Y down; origins in primary-scale pixels, sizes in each screen's own pixels)
/// back to AppKit points (bottom-left origin, Y up) for window placement. Main thread only (reads NSScreen).
/// </summary>
internal static class ScreenGeometry
{
    /// <summary>Converts a pixel rect to an AppKit frame; <paramref name="scale"/> is the backing scale of the
    /// screen the rect lands on (the primary when it lands on none), i.e. the layer's contentsScale.</summary>
    public static CGRect ToPoints(MonitorRect pixels, out double scale)
    {
        var screens = NSScreen.Screens;
        var primary = screens[0];
        var primaryScale = (double)primary.BackingScaleFactor;
        var primaryTopPoints = (double)primary.Frame.Y + (double)primary.Frame.Height;

        // The screen whose pixel rect contains the target's top-left decides the size scale.
        scale = primaryScale;
        foreach (var screen in screens)
        {
            var s = (double)screen.BackingScaleFactor;
            var left = (double)screen.Frame.X * primaryScale;
            var top = (primaryTopPoints - ((double)screen.Frame.Y + (double)screen.Frame.Height)) * primaryScale;
            var width = (double)screen.Frame.Width * s;
            var height = (double)screen.Frame.Height * s;
            if (pixels.X >= left && pixels.X < left + width && pixels.Y >= top && pixels.Y < top + height)
            {
                scale = s;
                break;
            }
        }

        var widthPoints = pixels.Width / scale;
        var heightPoints = pixels.Height / scale;
        var xPoints = pixels.X / primaryScale;
        var yPoints = primaryTopPoints - pixels.Y / primaryScale - heightPoints;
        return new CGRect(xPoints, yPoints, widthPoints, heightPoints);
    }
}
