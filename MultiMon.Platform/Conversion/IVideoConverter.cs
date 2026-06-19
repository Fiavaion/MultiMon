using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiMon.Platform.Conversion;

/// <summary>
/// HAP codec variants. The ffmpeg hap encoder's -format flag takes the lowercase value (hap, hap_alpha,
/// hap_q, hap_q_alpha); see <see cref="FfmpegHapConverter"/> for the mapping.
/// </summary>
public enum HapFormat
{
    /// <summary>Standard HAP — DXT1 compressed, smallest files, no alpha.</summary>
    Hap,
    /// <summary>HAP Alpha — DXT5 compressed, supports an alpha channel.</summary>
    HapAlpha,
    /// <summary>HAP Q — scaled YCoCg in DXT5, best quality. The recommended default for most content.</summary>
    HapQ,
    /// <summary>HAP Q Alpha — scaled YCoCg + alpha, highest quality with transparency.</summary>
    HapQAlpha,
}

/// <summary>
/// Progress snapshot reported by <see cref="IVideoConverter.ConvertToHapAsync"/> at each FFmpeg stderr line.
/// </summary>
public sealed class ConversionProgress
{
    public double PercentComplete { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? EstimatedRemaining { get; init; }
    public int FramesProcessed { get; init; }
    public double CurrentFps { get; init; }
}

/// <summary>
/// Result returned when <see cref="IVideoConverter.ConvertToHapAsync"/> finishes (success or failure).
/// </summary>
public sealed class ConversionResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public long OutputFileSizeBytes { get; init; }
}

/// <summary>
/// Parameters for a single-file HAP conversion.
/// </summary>
public sealed class ConversionOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>HAP variant to encode. Default: <see cref="HapFormat.HapQ"/>.</summary>
    public HapFormat Format { get; init; } = HapFormat.HapQ;

    /// <summary>Chunk count passed to -chunks. Affects parallel-decode performance on playback. Default: 4.</summary>
    public int ChunkCount { get; init; } = 4;
}

/// <summary>
/// Video file metadata returned by <see cref="IVideoConverter.GetVideoMetadataAsync"/>.
/// </summary>
public sealed class VideoMetadata
{
    public TimeSpan Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public string? Codec { get; init; }
    public string? PixelFormat { get; init; }
    public long FileSizeBytes { get; init; }
}

/// <summary>
/// Offline HAP-encode utility service. This is an FFmpeg shell-out for pre-converting source video to
/// HAP .mov — it is completely separate from the real-time decode/render pipeline and does not touch
/// IPerformanceController, MasterClock, or any D3D11 type.
/// </summary>
public interface IVideoConverter
{
    /// <summary>
    /// Returns the resolved path to ffmpeg.exe, or <c>null</c> if FFmpeg cannot be found.
    /// Searches: app-bundled ffmpeg\ffmpeg.exe → PATH → common install locations.
    /// </summary>
    string? GetFfmpegPath();

    /// <summary>Runs <c>ffmpeg -version</c> to confirm the binary is functional.</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Convert <paramref name="options"/>.InputPath to a HAP .mov at <paramref name="options"/>.OutputPath.
    /// Progress is reported for each parsed FFmpeg stderr line. Cancellation kills the process and deletes
    /// the partial output file.
    /// </summary>
    Task<ConversionResult> ConvertToHapAsync(
        ConversionOptions options,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probe a video file with ffprobe (JSON) or fall back to parsing <c>ffmpeg -i</c> stderr.
    /// Returns <c>null</c> if the file cannot be read or FFmpeg is unavailable.
    /// </summary>
    Task<VideoMetadata?> GetVideoMetadataAsync(string filePath);

    /// <summary>Returns true when ffprobe identifies the file's video codec as a HAP variant.</summary>
    Task<bool> IsHapVideoAsync(string filePath);
}
