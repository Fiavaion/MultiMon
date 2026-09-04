using System.Management;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Platform;

/// <summary>
/// Detects the GPU (vendor + name) and derives two capability decisions from it:
/// <list type="bullet">
///   <item><see cref="PreferSoftwareDecode"/> — true for GPUs on the <see cref="FlakyHardwareDecodeGpus"/>
///   list (or when MULTIMON_FORCE_SW_DECODE is set). The list is the old app's D3D11VA blacklist: on those
///   generations hardware video decode is unreliable, so Media Foundation should take its software path.</item>
///   <item><see cref="SupportsHap"/> — true unless MULTIMON_DISABLE_HAP is set. HAP needs BC1/BC3/BC4/BC7
///   texture support, which is MANDATORY at feature level 11.0 (the device floor), so no GPU that can create
///   our device lacks it; the env var is the user escape hatch and the way to exercise the HAP→MF fallback.</item>
/// </list>
/// Detection order: WMI (largest-AdapterRAM controller) before the device exists; once the D3D11 device is
/// created the Graphics layer calls <see cref="ApplyDeviceAdapter"/> with the DEVICE's adapter, which wins —
/// on Optimus/hybrid rigs WMI's biggest-VRAM guess and the adapter actually rendering can differ.
/// Fail-safe: any detection error leaves the vendor Unknown, HAP enabled, and hardware decode preferred.
/// </summary>
public static class GpuCapabilityService
{
    private static readonly object _lock = new();
    private static volatile bool _detected;
    private static string _gpuName = "Unknown";
    private static GpuVendor _vendor = GpuVendor.Unknown;

    // PCI vendor ids as reported in DXGI_ADAPTER_DESC.VendorId.
    private const uint PciVendorNvidia = 0x10DE;
    private const uint PciVendorAmd = 0x1002;
    private const uint PciVendorIntel = 0x8086;

    /// <summary>
    /// GPUs whose D3D11 hardware video decode (D3D11VA) is known-flaky — the old app's blacklist, kept for
    /// the same reason: on a match, decode should prefer Media Foundation's software path. Format:
    /// "Vendor|Model" (case-insensitive Contains on both halves). This list does NOT gate HAP: BCn texture
    /// formats are mandatory at FL 11.0 and every GPU here that reaches FL 11.0 handles them.
    /// </summary>
    private static readonly string[] FlakyHardwareDecodeGpus =
    {
        // Older AMD FirePro — unreliable D3D11VA.
        "AMD|FirePro W600", "AMD|FirePro W5000", "AMD|FirePro W7000",
        "AMD|FirePro V3800", "AMD|FirePro V4800", "AMD|FirePro V5800",

        // Pre-Kepler NVIDIA.
        "NVIDIA|Quadro 600", "NVIDIA|Quadro 2000",

        // Pre-Skylake Intel iGPUs (Sandy Bridge → Broadwell, 2011–2015). Skylake (HD 510/520/530,
        // 2015+) and later are NOT listed — their D3D11VA is fine.
        "Intel|HD Graphics 2000", "Intel|HD Graphics 3000",   // Sandy Bridge
        "Intel|HD Graphics 2500", "Intel|HD Graphics 4000",   // Ivy Bridge
        "Intel|HD Graphics 4200", "Intel|HD Graphics 4400", "Intel|HD Graphics 4600",
        "Intel|HD Graphics 5000", "Intel|Iris Graphics 5100", "Intel|Iris Pro Graphics 5200", // Haswell
        "Intel|HD Graphics 5300", "Intel|HD Graphics 5500", "Intel|HD Graphics 6000",
        "Intel|Iris Graphics 6100", "Intel|Iris Pro Graphics 6200", // Broadwell
    };

    public static string DetectedGpuName { get { EnsureDetected(); return _gpuName; } }
    public static GpuVendor DetectedVendor { get { EnsureDetected(); return _vendor; } }

    /// <summary>True unless MULTIMON_DISABLE_HAP is set: the BCn formats HAP needs are mandatory at the
    /// device's FL 11.0 floor (the Graphics layer additionally checks the exact format per clip). The env var
    /// is a user escape hatch and the way to exercise the HAP-unsupported → Media Foundation fallback on any GPU.</summary>
    public static bool SupportsHap => !IsEnvSet("MULTIMON_DISABLE_HAP");

    /// <summary>True when Media Foundation should take its SOFTWARE decode path: the detected GPU is on the
    /// flaky-D3D11VA list, or MULTIMON_FORCE_SW_DECODE is set (cross-GPU testing hook).</summary>
    public static bool PreferSoftwareDecode
    {
        get
        {
            if (IsEnvSet("MULTIMON_FORCE_SW_DECODE"))
                return true;
            EnsureDetected();
            return IsHardwareDecodeFlaky(_gpuName);
        }
    }

    /// <summary>
    /// Overrides detection with the adapter the D3D11 device was actually created on (from the Graphics layer,
    /// once the device exists). The vendor comes from the PCI id, not the name. Wins over the WMI guess.
    /// </summary>
    public static void ApplyDeviceAdapter(string description, uint vendorId, ILog? log = null)
    {
        lock (_lock)
        {
            var previous = _detected ? $"'{_gpuName}' ({_vendor})" : "(not yet detected)";
            _gpuName = string.IsNullOrWhiteSpace(description) ? "Unknown" : description;
            _vendor = VendorFromPciId(vendorId);
            _detected = true;
            log?.Info("Gpu", $"GPU detection set from the D3D11 device adapter: '{_gpuName}' vendor={_vendor} (pciVendorId=0x{vendorId:X4}); " +
                             $"WMI pre-device detection was {previous}. HAP={(SupportsHap ? "enabled" : "disabled (MULTIMON_DISABLE_HAP)")}, " +
                             $"preferSoftwareDecode={PreferSoftwareDecode}.");
        }
    }

    /// <summary>Maps a DXGI/PCI vendor id to <see cref="GpuVendor"/> (0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel; else Unknown).</summary>
    public static GpuVendor VendorFromPciId(uint vendorId) => vendorId switch
    {
        PciVendorNvidia => GpuVendor.Nvidia,
        PciVendorAmd => GpuVendor.Amd,
        PciVendorIntel => GpuVendor.Intel,
        _ => GpuVendor.Unknown,
    };

    /// <summary>Pure: true when <paramref name="gpuName"/> matches the flaky-hardware-decode list ("Vendor|Model",
    /// case-insensitive Contains on both halves).</summary>
    public static bool IsHardwareDecodeFlaky(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return false;
        var g = gpuName.ToLowerInvariant();
        foreach (var entry in FlakyHardwareDecodeGpus)
        {
            var parts = entry.Split('|');
            if (parts.Length != 2) continue;
            if (g.Contains(parts[0].ToLowerInvariant()) && g.Contains(parts[1].ToLowerInvariant()))
                return true;
        }
        return false;
    }

    /// <summary>Logs EVERY GPU adapter (name, VRAM, driver version/date) — not just the chosen primary.
    /// On a multi-GPU box this is the inventory you need to tell which adapter a problem came from.
    /// Best-effort: WMI faults are logged, never thrown.</summary>
    public static void LogAdapters(ILog log)
    {
        EnsureDetected();
        log.Info("Gpu", $"Primary GPU: '{_gpuName}' (vendor={_vendor}, HAP={(SupportsHap ? "enabled" : "disabled")}, preferSoftwareDecode={PreferSoftwareDecode}).");
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

    /// <summary>Run GPU detection once (thread-safe). Safe to call early at app start (pre-device WMI path).</summary>
    public static void EnsureDetected()
    {
        if (_detected) return;
        lock (_lock)
        {
            if (_detected) return;
            Detect();
        }
    }

    private static bool IsEnvSet(string name) => Environment.GetEnvironmentVariable(name) is "1" or "true";

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

            // Prefer the GPU with the most adapter RAM (usually the discrete one). A guess until the device
            // exists — ApplyDeviceAdapter then replaces it with the adapter that actually renders.
            var primary = gpus.OrderByDescending(g =>
            {
                try { return g["AdapterRAM"] is { } r ? Convert.ToUInt64(r) : 0UL; }
                catch { return 0UL; }
            }).First();

            _gpuName = primary["Name"]?.ToString() ?? "Unknown";
            _vendor = VendorFromName(_gpuName);
        }
        catch (Exception ex)
        {
            _gpuName = $"Unknown (detection error: {ex.Message})";
            _vendor = GpuVendor.Unknown;
        }
    }

    /// <summary>Pure: vendor from a WMI/DXGI description string (the pre-device fallback; PCI id is authoritative).</summary>
    public static GpuVendor VendorFromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro") || n.Contains("rtx") || n.Contains("gtx"))
            return GpuVendor.Nvidia;
        if (n.Contains("amd") || n.Contains("radeon") || n.Contains("firepro"))
            return GpuVendor.Amd;
        if (n.Contains("intel")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }
}
