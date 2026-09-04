using MultiMon.Core.Models;
using MultiMon.Platform;

namespace MultiMon.Tests;

/// <summary>Pure-logic coverage of GpuCapabilityService: PCI-id / name → vendor, and the flaky-D3D11VA list.</summary>
public class GpuCapabilityServiceTests
{
    [Theory]
    [InlineData(0x10DEu, GpuVendor.Nvidia)]
    [InlineData(0x1002u, GpuVendor.Amd)]
    [InlineData(0x8086u, GpuVendor.Intel)]
    [InlineData(0x1414u, GpuVendor.Unknown)] // Microsoft (WARP / Basic Render Driver)
    [InlineData(0u, GpuVendor.Unknown)]
    public void VendorFromPciId_maps_known_ids(uint pciId, GpuVendor expected)
        => Assert.Equal(expected, GpuCapabilityService.VendorFromPciId(pciId));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3080 Laptop GPU", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon(TM) Graphics", GpuVendor.Amd)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Render Driver", GpuVendor.Unknown)]
    public void VendorFromName_maps_descriptions(string name, GpuVendor expected)
        => Assert.Equal(expected, GpuCapabilityService.VendorFromName(name));

    [Theory]
    [InlineData("Intel(R) HD Graphics 4000", true)]          // Ivy Bridge — listed
    [InlineData("intel hd graphics 4600", true)]             // case-insensitive
    [InlineData("AMD FirePro W5000", true)]
    [InlineData("NVIDIA Quadro 2000", true)]
    [InlineData("Intel(R) HD Graphics 530", false)]          // Skylake — deliberately NOT listed
    [InlineData("Intel(R) UHD Graphics 620", false)]
    [InlineData("NVIDIA GeForce RTX 3080 Laptop GPU", false)]
    [InlineData("AMD Radeon(TM) Graphics", false)]
    [InlineData("HD Graphics 4000", false)]                  // model without the vendor half does not match
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHardwareDecodeFlaky_matches_vendor_and_model_halves(string? name, bool expected)
        => Assert.Equal(expected, GpuCapabilityService.IsHardwareDecodeFlaky(name));

    [Fact]
    public void ApplyDeviceAdapter_overrides_detection_with_pci_vendor()
    {
        GpuCapabilityService.ApplyDeviceAdapter("Some Card", 0x1002);
        Assert.Equal("Some Card", GpuCapabilityService.DetectedGpuName);
        Assert.Equal(GpuVendor.Amd, GpuCapabilityService.DetectedVendor);

        // The name-based guess is not consulted once a PCI id is supplied.
        GpuCapabilityService.ApplyDeviceAdapter("NVIDIA-looking name", 0x8086);
        Assert.Equal(GpuVendor.Intel, GpuCapabilityService.DetectedVendor);
    }
}
