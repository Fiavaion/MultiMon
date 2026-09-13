using System.Runtime.InteropServices;
using Foundation;
using MultiMon.Core.Diagnostics;
using ObjCRuntime;

namespace MultiMon.Platform.Mac;

/// <summary>Outcome of one <see cref="DisplayModeService.Match"/> call. Never an exception: the perform path
/// must carry on whatever the display does.</summary>
public enum DisplayMatchOutcome
{
    /// <summary>The display was switched to a mode whose rate is an integer multiple of the clip's fps.</summary>
    Matched,
    /// <summary>The current mode already satisfies the clip; nothing was done.</summary>
    AlreadyMatched,
    /// <summary>No mode with the current pixel AND point size has a rate that is a multiple of the fps.</summary>
    NoSuitableMode,
    /// <summary>A suitable mode exists but CoreGraphics refused the switch (reason in <see cref="DisplayMatchResult.Reason"/>).</summary>
    Failed
}

/// <summary>One display's mode as CoreGraphics reports it: the IOKit mode id (what "same mode" compares on), pixel
/// size, point size and refresh rate (0 when the panel publishes none).</summary>
public readonly record struct DisplayModeInfo(int ModeId, int PixelWidth, int PixelHeight, int Width, int Height, double RefreshRate)
{
    public override string ToString() => $"{PixelWidth}x{PixelHeight}px ({Width}x{Height}pt) {RefreshRate:0.##}Hz mode#{ModeId}";
}

public readonly record struct DisplayMatchResult(DisplayMatchOutcome Outcome, DisplayModeInfo Before, DisplayModeInfo After, string Reason)
{
    public override string ToString() => Outcome switch
    {
        DisplayMatchOutcome.Matched => $"{Before.RefreshRate:0.##} Hz -> {After.RefreshRate:0.##} Hz",
        DisplayMatchOutcome.AlreadyMatched => $"already {Before.RefreshRate:0.##} Hz",
        DisplayMatchOutcome.NoSuitableMode => $"stayed at {Before.RefreshRate:0.##} Hz: {Reason}",
        _ => $"stayed at {Before.RefreshRate:0.##} Hz: FAILED {Reason}"
    };
}

/// <summary>
/// Matches a display's refresh rate to a clip's frame rate for the length of a perform (25 fps into a 60 Hz panel
/// is a 2-3 pull-down stutter; into 50 or 100 Hz it is clean) and restores the original mode afterwards. The switch
/// keeps the display's pixel size AND point size, so the persistent output windows neither move nor resize and the
/// Core monitor geometry stays valid — only the rate changes. Quartz Display Services P/Invokes, same pattern as
/// <see cref="MonitorService"/>; every CF object is released and every CGError is checked and logged.
/// <para>Ownership: the original mode of every display this service switched is retained until
/// <see cref="Restore"/> / <see cref="RestoreAll"/> release it. The configuration is committed
/// <c>kCGConfigureForAppOnly</c>, so the OS also reverts it if the process dies mid-perform. Any thread except the
/// render thread may call in (CoreGraphics reconfiguration is thread-safe; the caller must keep the main run loop
/// pumping because AppKit consumes the resulting screen-parameters notification there).</para>
/// </summary>
public sealed class DisplayModeService : IDisposable
{
    /// <summary>|rate − k·fps| within this is "a multiple": 59.94 for 29.97, 47.95 for 23.976, 59.94 vs 60 is NOT.</summary>
    private const double RateToleranceHz = 0.06;

    private readonly ILog? _log;
    private readonly Dictionary<uint, IntPtr> _originalModes = new(); // display → retained CGDisplayModeRef
    private readonly object _gate = new();

    public DisplayModeService(ILog? log = null) => _log = log;

    /// <summary>The display's current mode, or null when CoreGraphics has none for it (disconnected, asleep).</summary>
    public DisplayModeInfo? QueryCurrent(uint displayId)
    {
        var mode = CGDisplayCopyDisplayMode(displayId);
        if (mode == IntPtr.Zero)
            return null;
        try { return Describe(mode); }
        finally { CGDisplayModeRelease(mode); }
    }

    /// <summary>True when <paramref name="rateHz"/> is an integer multiple of <paramref name="fps"/> within tolerance.</summary>
    public static bool IsMultiple(double rateHz, double fps)
    {
        if (fps <= 0 || rateHz <= 0)
            return false;
        var k = Math.Round(rateHz / fps);
        return k >= 1 && Math.Abs(rateHz - k * fps) <= RateToleranceHz;
    }

    /// <summary>
    /// Switches <paramref name="displayId"/> to the highest-rate mode with its current pixel and point size whose rate
    /// is a multiple of <paramref name="fps"/>. Idempotent per display: a second call while switched compares against
    /// the ORIGINAL mode and re-targets from there, so a show change mid-session never loses the restore point.
    /// </summary>
    public DisplayMatchResult Match(uint displayId, double fps)
    {
        var none = new DisplayModeInfo(0, 0, 0, 0, 0, 0);
        if (fps <= 0)
            return new DisplayMatchResult(DisplayMatchOutcome.NoSuitableMode, none, none, "clip frame rate unknown");

        lock (_gate)
        {
            var current = CGDisplayCopyDisplayMode(displayId);
            if (current == IntPtr.Zero)
                return new DisplayMatchResult(DisplayMatchOutcome.Failed, none, none, $"display {displayId} has no current mode");

            IntPtr best = IntPtr.Zero;
            IntPtr modes = IntPtr.Zero;
            try
            {
                var now = Describe(current);
                if (IsMultiple(now.RefreshRate, fps) && !_originalModes.ContainsKey(displayId))
                    return new DisplayMatchResult(DisplayMatchOutcome.AlreadyMatched, now, now, string.Empty);

                // Scaled ("More Space") modes only appear with kCGDisplayShowDuplicateLowResolutionModes; without it
                // the list holds one entry per pixel size and the point-size match below would find nothing. The key
                // must be CoreGraphics' own exported CFString — a same-spelled literal is silently ignored (found by
                // the first run: zero candidates for a mode the display was running in).
                using var options = new NSDictionary(ShowDuplicateLowResolutionModesKey, NSNumber.FromBoolean(true));
                modes = CGDisplayCopyAllDisplayModes(displayId, options.Handle);
                if (modes == IntPtr.Zero)
                    return new DisplayMatchResult(DisplayMatchOutcome.Failed, now, now, $"display {displayId}: CGDisplayCopyAllDisplayModes returned null");

                var count = CFArrayGetCount(modes);
                var bestRate = 0.0;
                var candidates = new List<string>();
                for (nint i = 0; i < count; i++)
                {
                    var mode = CFArrayGetValueAtIndex(modes, i); // borrowed from the array; not released here
                    var info = Describe(mode);
                    if (info.PixelWidth != now.PixelWidth || info.PixelHeight != now.PixelHeight ||
                        info.Width != now.Width || info.Height != now.Height || !CGDisplayModeIsUsableForDesktopGUI(mode))
                        continue;
                    candidates.Add($"{info.RefreshRate:0.##}");
                    if (IsMultiple(info.RefreshRate, fps) && info.RefreshRate > bestRate)
                    {
                        bestRate = info.RefreshRate;
                        best = mode;
                    }
                }

                if (candidates.Count == 0)
                    return new DisplayMatchResult(DisplayMatchOutcome.Failed, now, now,
                        $"CGDisplayCopyAllDisplayModes listed {count} mode(s) but none at the current {now.PixelWidth}x{now.PixelHeight}px/{now.Width}x{now.Height}pt (enumeration options not honoured?)");
                if (best == IntPtr.Zero)
                    return new DisplayMatchResult(DisplayMatchOutcome.NoSuitableMode, now, now,
                        $"no {now.PixelWidth}x{now.PixelHeight}px/{now.Width}x{now.Height}pt mode is a multiple of {fps:0.###} fps (rates: {string.Join("/", candidates)})");
                if (IsMultiple(now.RefreshRate, fps) && Math.Abs(now.RefreshRate - bestRate) <= RateToleranceHz)
                    return new DisplayMatchResult(DisplayMatchOutcome.AlreadyMatched, now, now, string.Empty);

                var err = CGBeginDisplayConfiguration(out var config);
                if (err != CGError.Success)
                    return new DisplayMatchResult(DisplayMatchOutcome.Failed, now, now, $"CGBeginDisplayConfiguration: {err}");
                err = CGConfigureDisplayWithDisplayMode(config, displayId, best, IntPtr.Zero);
                if (err != CGError.Success)
                {
                    CGCancelDisplayConfiguration(config);
                    return new DisplayMatchResult(DisplayMatchOutcome.Failed, now, now, $"CGConfigureDisplayWithDisplayMode: {err}");
                }
                err = CGCompleteDisplayConfiguration(config, CGConfigureOption.ForAppOnly);
                if (err != CGError.Success)
                    return new DisplayMatchResult(DisplayMatchOutcome.Failed, now, now, $"CGCompleteDisplayConfiguration: {err}");

                // First switch of this display: keep the mode we found it in. A re-target keeps the older one.
                if (!_originalModes.ContainsKey(displayId))
                {
                    _originalModes[displayId] = current;
                    current = IntPtr.Zero; // ownership moved to the dictionary
                }
                var after = QueryCurrent(displayId) ?? Describe(best);
                _log?.Info("DisplayMode", $"display {displayId}: {now} -> {after} for {fps:0.###} fps");
                return new DisplayMatchResult(DisplayMatchOutcome.Matched, now, after, string.Empty);
            }
            finally
            {
                if (modes != IntPtr.Zero) CFRelease(modes);
                if (current != IntPtr.Zero) CGDisplayModeRelease(current);
            }
        }
    }

    /// <summary>Puts the display back in the mode <see cref="Match"/> found it in. No-op if it was never switched.</summary>
    public void Restore(uint displayId)
    {
        lock (_gate)
        {
            if (!_originalModes.Remove(displayId, out var original))
                return;
            try
            {
                var target = Describe(original);
                var now = QueryCurrent(displayId);
                if (now is { } n && n.ModeId == target.ModeId)
                {
                    _log?.Info("DisplayMode", $"display {displayId}: already back at {target} (the OS restored it)");
                    return;
                }
                var err = CGBeginDisplayConfiguration(out var config);
                if (err == CGError.Success)
                {
                    err = CGConfigureDisplayWithDisplayMode(config, displayId, original, IntPtr.Zero);
                    if (err == CGError.Success)
                        err = CGCompleteDisplayConfiguration(config, CGConfigureOption.ForAppOnly);
                    else
                        CGCancelDisplayConfiguration(config);
                }
                if (err == CGError.Success)
                    _log?.Info("DisplayMode", $"display {displayId}: restored {now?.ToString() ?? "?"} -> {target}");
                else
                    _log?.Error("DisplayMode", $"display {displayId}: restore to {target} failed: {err}");
            }
            finally
            {
                CGDisplayModeRelease(original);
            }
        }
    }

    /// <summary>Restores every display this service switched. Idempotent.</summary>
    public void RestoreAll()
    {
        uint[] ids;
        lock (_gate) ids = _originalModes.Keys.ToArray();
        foreach (var id in ids)
            Restore(id);
    }

    public void Dispose() => RestoreAll();

    /// <summary>CoreGraphics' exported <c>kCGDisplayShowDuplicateLowResolutionModes</c> CFString, read once.</summary>
    private static readonly NSString ShowDuplicateLowResolutionModesKey = LoadKey();

    private static NSString LoadKey()
    {
        var lib = Dlfcn.dlopen(CoreGraphicsLib, 0);
        if (lib == IntPtr.Zero)
            throw new DllNotFoundException(CoreGraphicsLib);
        try
        {
            var key = Dlfcn.GetStringConstant(lib, "kCGDisplayShowDuplicateLowResolutionModes");
            return key ?? throw new EntryPointNotFoundException("kCGDisplayShowDuplicateLowResolutionModes");
        }
        finally
        {
            Dlfcn.dlclose(lib);
        }
    }

    private static DisplayModeInfo Describe(IntPtr mode) => new(
        CGDisplayModeGetIODisplayModeID(mode),
        (int)CGDisplayModeGetPixelWidth(mode), (int)CGDisplayModeGetPixelHeight(mode),
        (int)CGDisplayModeGetWidth(mode), (int)CGDisplayModeGetHeight(mode),
        CGDisplayModeGetRefreshRate(mode));

    // ── Quartz Display Services ──────────────────────────────────────────────

    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private enum CGError { Success = 0 }
    private enum CGConfigureOption : uint { ForAppOnly = 0, ForSession = 1, Permanently = 2 }

    [DllImport(CoreGraphicsLib)] private static extern IntPtr CGDisplayCopyDisplayMode(uint display);
    [DllImport(CoreGraphicsLib)] private static extern IntPtr CGDisplayCopyAllDisplayModes(uint display, IntPtr options);
    [DllImport(CoreGraphicsLib)] private static extern void CGDisplayModeRelease(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern double CGDisplayModeGetRefreshRate(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern nuint CGDisplayModeGetPixelHeight(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern nuint CGDisplayModeGetWidth(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern nuint CGDisplayModeGetHeight(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern int CGDisplayModeGetIODisplayModeID(IntPtr mode);
    [DllImport(CoreGraphicsLib)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGDisplayModeIsUsableForDesktopGUI(IntPtr mode);
    [DllImport(CoreGraphicsLib)] private static extern CGError CGBeginDisplayConfiguration(out IntPtr config);
    [DllImport(CoreGraphicsLib)] private static extern CGError CGConfigureDisplayWithDisplayMode(IntPtr config, uint display, IntPtr mode, IntPtr options);
    [DllImport(CoreGraphicsLib)] private static extern CGError CGCompleteDisplayConfiguration(IntPtr config, CGConfigureOption option);
    [DllImport(CoreGraphicsLib)] private static extern CGError CGCancelDisplayConfiguration(IntPtr config);
    [DllImport(CoreFoundationLib)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundationLib)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundationLib)] private static extern void CFRelease(IntPtr cf);
}
