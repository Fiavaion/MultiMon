namespace MultiMon.Core.Models;

/// <summary>
/// A WASAPI render endpoint a track can be routed to. Salvaged in SHAPE from the old app
/// (REBUILD_ARCHITECTURE §7) minus the unused channel-configuration enum; populated from the
/// Core Audio device enumeration in <c>MultiMon.Audio</c>.
/// </summary>
public sealed class AudioOutputDevice
{
    /// <summary>WASAPI endpoint id (the IMMDevice id string). Stable across sessions for a given device.</summary>
    public required string Id { get; init; }

    /// <summary>Friendly device name for UI/logging.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Channel count of the device mix format (e.g. 2 for stereo).</summary>
    public int ChannelCount { get; init; }

    /// <summary>Sample rate of the device mix format, in Hz (e.g. 48000).</summary>
    public int SampleRate { get; init; }

    /// <summary>True when this is the system default render endpoint.</summary>
    public bool IsDefault { get; init; }

    public override string ToString() => $"{Name} ({ChannelCount}ch @ {SampleRate}Hz){(IsDefault ? " [default]" : "")}";
}
