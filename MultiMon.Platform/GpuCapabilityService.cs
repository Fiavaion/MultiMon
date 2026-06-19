using System.Management;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Platform;

/// <summary>
/// Detects the primary GPU (WMI) and its vendor, and gates HAP capability via a blacklist of GPUs
/// that handle the compressed-texture / BC7 path unreliably (pre-Skylake Intel iGPUs and some old
/// AMD FirePro / NVIDIA cards). Salvaged from the old app's GpuCompatibilityService and repurposed:
/// the blacklist now gates the HAP enhancement (BCn upload + YCoCg shader), NOT the dropped LibVLC
/// backend ladder. Fail-safe: any detection error leaves vendor Unknown and HAP disabled.
/// </summary>
public static class GpuCapabilityService
{
    private static readonly object _lock = new();
    private static volatile bool _detected;
    private static string _gpuName = "Unknown";
    private static GpuVendor _vendor = GpuVendor.Unknown;

    /// <summary>
    /// GPUs known to handle compressed-texture/BC7 HAP playback unreliably. A match disables the HAP
    /// enhancement (the source falls back to Media Foundation decode). Format: "Vendor|Model"
    /// (case-insensitive Contains on both halves).
    /// </summary>
    private static readonly string[] HapUnreliableGpus =
    {
        // Older AMD FirePro — flaky compressed-texture / DXGI behaviour.
        "AMD|FirePro W600", "AMD|FirePro W5000", "AMD|FirePro W7000",
        "AMD|FirePro V3800", "AMD|FirePro V4800", "AMD|FirePro V5800",

        // Pre-Kepler NVIDIA.
        "NVIDIA|Quadro 600", "NVIDIA|Quadro 2000",

        // Pre-Skylake Intel iGPUs (Sandy Bridge → Broadwell, 2011–2015). Skylake (HD 510/520/530,
        // 2015+) and later are NOT listed — they handle the modern texture path fine.
        "Intel|HD Graphics 2000", "Intel|HD Graphics 3000",   // Sandy Bridge
        "Intel|HD Graphics 2500", "Intel|HD Graphics 4000",   // Ivy Bridge
        "Intel|HD Graphics 4200", "Intel|HD Graphics 4400", "Intel|HD Graphics 4600",
        "Intel|HD Graphics 5000", "Intel|Iris Graphics 5100", "Intel|Iris Pro Graphics 5200", // Haswell
        "Intel|HD Graphics 5300", "Intel|HD Graphics 5500", "Intel|HD Graphics 6000",
        "Intel|Iris Graphics 6100", "Intel|Iris Pro Graphics 6200", // Broadwell
    };

    public static string DetectedGpuName { get { EnsureDetected(); return _gpuName; } }
    public static GpuVendor DetectedVendor { get { EnsureDetected(); return _vendor; } }

    /// <summary>True if the detected GPU is NOT on the HAP-unreliable blacklist. HAP is an
    /// enhancement: when this is false, sources fall back to Media Foundation decode (never crash).
    /// MULTIMON_DISABLE_HAP forces this false — a user escape hatch and the way to exercise the
    /// HAP-unsupported fallback on a GPU that actually supports HAP (cross-GPU testing of G1).</summary>
    public static bool SupportsHap
    {
        get
        {
            if (Environment.GetEnvironmentVariable("MULTIMON_DISABLE_HAP") is "1" or "true")
                return false;
            EnsureDetected();
            return !IsHapUnreliable(_gpuName);
        }
    }

    /// <summary>Logs EVERY GPU adapter (name, VRAM, driver version/date) — not just the chosen primary.
    /// On a multi-GPU box this is the inventory you need to tell which adapter a problem came from.
    /// Best-effort: WMI faults are logged, never thrown.</summary>
    public static void LogAdapters(ILog log)
    {
        EnsureDetected();
        log.Info("Gpu", $"Primary GPU: '{_gpuName}' (vendor={_vendor}, HAP={(SupportsHap ? "enabled" : "disabled")}).");
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, DriverVersion, DriverDate, VideoProcessor FROM Win32_VideoController");
            var i = 0;
            foreach (var g in searcher.Get().Cast<ManagementObject>())
            {
                i++;
                ulong ram = 0;
                try { ram = g["AdapterRAM"] is { } r ? Convert.ToUInt64(r) : 0UL; } catch { /* WMI sometimes returns a negative-typed RAM */ }
                log.Info("Gpu", $"  adapter[{i}]: '{g["Name"]}' vram={(ram > 0 ? $"{ram / (1024.0 * 1024 * 1024):0.0}GB" : "?")} " +
                    $"driver={g["DriverVersion"]} ({g["DriverDate"]}) processor='{g["VideoProcessor"]}'");
            }
            if (i == 0) log.Info("Gpu", "  WMI reported no video controllers.");
        }
        catch (Exception ex)
        {
            log.Error("Gpu", $"Adapter enumeration failed: {ex.Message}");
        }
    }

    /// <summary>Run GPU detection once (thread-safe). Safe to call early at app start.</summary>
    public static void EnsureDetected()
    {
        if (_detected) return;
        lock (_lock)
        {
            if (_detected) return;
            Detect();
        }
    }

    /// <summary>Force re-detection (tests).</summary>
    public static void ResetDetection()
    {
        lock (_lock)
        {
            _detected = false;
            _gpuName = "Unknown";
            _vendor = GpuVendor.Unknown;
        }
    }

    private static void Detect()
    {
        _detected = true;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
            var gpus = searcher.Get().Cast<ManagementObject>().ToList();
            if (gpus.Count == 0)
            {
                _gpuName = "Unknown (WMI returned no results)";
                _vendor = GpuVendor.Unknown;
                return;
            }

            // Prefer the GPU with the most adapter RAM (usually the discrete/primary one).
            var primary = gpus.OrderByDescending(g =>
            {
                try { return g["AdapterRAM"] is { } r ? Convert.ToUInt64(r) : 0UL; }
                catch { return 0UL; }
            }).First();

            _gpuName = primary["Name"]?.ToString() ?? "Unknown";
            _vendor = ParseVendor(_gpuName);
        }
        catch (Exception ex)
        {
            _gpuName = $"Unknown (detection error: {ex.Message})";
            _vendor = GpuVendor.Unknown;
        }
    }

    private static GpuVendor ParseVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx") || n.Contains("gtx"))
            return GpuVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon") || n.Contains("firepro"))
            return GpuVendor.Amd;
        if (n.Contains("intel")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    private static bool IsHapUnreliable(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return false;
        var g = gpuName.ToLowerInvariant();
        foreach (var entry in HapUnreliableGpus)
        {
            var parts = entry.Split('|');
            if (parts.Length != 2) continue;
            if (g.Contains(parts[0].ToLowerInvariant()) && g.Contains(parts[1].ToLowerInvariant()))
                return true;
        }
        return false;
    }
}
