namespace MultiMon.Core.Models;

/// <summary>
/// One audio track in a show: a source file routed to an output device, with a playback gain.
///
/// Salvaged in SHAPE from the old app's <c>AudioTrack</c> (REBUILD_ARCHITECTURE §3, §7) but rebuilt
/// lean — the LibVLC/WinRT <c>MediaPlayer</c>/<c>MediaSource</c> fields and the unused pan/channel
/// matrix are gone. Decode is Media Foundation → PCM; output is raw shared-mode WASAPI clocked to the
/// MasterClock (M6). Pan / multi-channel routing is added only when a mode actually needs it (M7 UI).
/// </summary>
public sealed class AudioTrack
{
    /// <summary>Stable id for this track within a show.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Display name (defaults to the file name).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Path to the source media file (audio file, or a video whose audio stream is used).</summary>
    public required string SourceFilePath { get; init; }

    /// <summary>Linear playback gain, 0.0 (silent) to 1.0 (unity). Clamped by the engine.</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>Stereo balance, -1.0 (full left) to +1.0 (full right), 0 = centre. Applied for 2-channel output.</summary>
    public double Pan { get; init; }

    /// <summary>When true the track is decoded but rendered silent (gain forced to zero).</summary>
    public bool IsMuted { get; init; }

    /// <summary>When any track in the show is soloed, only soloed tracks are audible (the rest are silenced).</summary>
    public bool IsSolo { get; init; }

    /// <summary>
    /// Target output device id (<see cref="AudioOutputDevice.Id"/>). Null routes to the default
    /// render endpoint — the only routing M6 exercises.
    /// </summary>
    public string? OutputDeviceId { get; init; }
}
