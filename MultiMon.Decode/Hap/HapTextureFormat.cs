namespace MultiMon.Decode.Hap;

/// <summary>
/// The compressed texture format a HAP frame decodes to — the low nibble of the HAP section type
/// byte (HAP spec). Each maps to a DXGI BCn format on upload (HapSource), and HapQ (<see cref="YCoCgDxt5"/>)
/// additionally needs the YCoCg→RGB pixel-shader variant. Values match the HAP on-disk constants.
/// </summary>
public enum HapTextureFormat
{
    /// <summary>RGB DXT1 / BC1 (plain "Hap").</summary>
    RgbDxt1 = 0x0B,

    /// <summary>RGBA DXT5 / BC3 ("Hap Alpha").</summary>
    RgbaDxt5 = 0x0E,

    /// <summary>Scaled YCoCg DXT5 / BC3 ("Hap Q") — needs the YCoCg→RGB shader.</summary>
    YCoCgDxt5 = 0x0F,

    /// <summary>RGBA BPTC UNORM / BC7 ("Hap 7" / "Hap Alpha-Only" variants).</summary>
    RgbaBptc = 0x01,

    /// <summary>A RGTC1 / BC4 (the alpha plane of "Hap Q Alpha").</summary>
    ARgtc1 = 0x0C
}
