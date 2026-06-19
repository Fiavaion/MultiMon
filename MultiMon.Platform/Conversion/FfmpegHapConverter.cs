using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MultiMon.Platform.Conversion;

/// <summary>
/// FFmpeg shell-out implementation of <see cref="IVideoConverter"/>. Converts a source video to a HAP .mov
/// by spawning <c>ffmpeg.exe</c> as a child process, parsing progress from stderr, and reporting it through
/// an <see cref="IProgress{T}"/> callback.
///
/// <para>This is a pure offline utility — it does not interact with the D3D11 pipeline, MasterClock, or
/// any playback service. It is safe to instantiate and call from any thread.</para>
/// </summary>
public sealed class FfmpegHapConverter : IVideoConverter
{
    // Cached after the first successful lookup so repeated calls don't re-walk PATH.
    private string? _ffmpegPath;
    private string? _ffprobePath;

    // Regex patterns for parsing FFmpeg stderr output.
    private static readonly Regex DurationRx = new(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled);
    private static readonly Regex FrameRx = new(@"frame=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex FpsRx = new(@"\sfps=\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex CodecRx = new(@"Video:\s*(\w+)", RegexOptions.Compiled);
    private static readonly Regex ResRx = new(@"(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
    private static readonly Regex FrateRx = new(@"([\d.]+)\s*fps", RegexOptions.Compiled);

    // ── Path resolution ────────────────────────────────────────────────────────

    public string? GetFfmpegPath()
    {
        if (_ffmpegPath is not null)
            return _ffmpegPath;

        // 1. Bundled alongside the app (highest priority — reproducible environment).
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (TryCachePaths(bundled)) return _ffmpegPath;

        // 2. On PATH (covers system-wide FFmpeg installs).
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (TryCachePaths(Path.Combine(dir, "ffmpeg.exe"))) return _ffmpegPath;
        }

        // 3. Common manual install locations.
        string[] commonPaths =
        [
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "ffmpeg", "bin", "ffmpeg.exe"),
        ];

        foreach (var p in commonPaths)
            if (TryCachePaths(p)) return _ffmpegPath;

        return null;
    }

    /// <summary>If <paramref name="ffmpegExe"/> exists, cache it (and its ffprobe sibling) and return true.</summary>
    private bool TryCachePaths(string ffmpegExe)
    {
        if (!File.Exists(ffmpegExe)) return false;

        _ffmpegPath = ffmpegExe;
        var dir = Path.GetDirectoryName(ffmpegExe)!;
        var probe = Path.Combine(dir, "ffprobe.exe");
        _ffprobePath = File.Exists(probe) ? probe : null;
        return true;
    }

    public async Task<bool> IsAvailableAsync()
    {
        var path = GetFfmpegPath();
        if (path is null) return false;

        try
        {
            using var proc = StartProcess(path, "-version");
            proc.Start();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Conversion ─────────────────────────────────────────────────────────────

    public async Task<ConversionResult> ConvertToHapAsync(
        ConversionOptions options,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var ffmpegPath = GetFfmpegPath();
        if (ffmpegPath is null)
        {
            return Fail("FFmpeg not found. Install it or place ffmpeg.exe in an 'ffmpeg' folder next to the app.",
                        stopwatch.Elapsed);
        }

        if (!File.Exists(options.InputPath))
            return Fail($"Input file not found: {options.InputPath}", stopwatch.Elapsed);

        // Probe the source so we can compute percent-complete from time= lines.
        var meta = await GetVideoMetadataAsync(options.InputPath);
        var totalSeconds = meta?.Duration.TotalSeconds ?? 0;

        // Map enum → ffmpeg -format string.
        var hapVariant = options.Format switch
        {
            HapFormat.Hap      => "hap",
            HapFormat.HapAlpha => "hap_alpha",
            HapFormat.HapQ     => "hap_q",
            HapFormat.HapQAlpha => "hap_q_alpha",
            _ => "hap_q",
        };

        // Ensure the output directory exists before ffmpeg tries to write to it.
        var outDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            Directory.CreateDirectory(outDir);

        // -y overwrite · -i input · -c:v hap · -format <variant> · -chunks <n> (parallel-decode chunk count)
        // · -c:a copy (audio passthrough) · output. Discrete args (ArgumentList) — no hand-quoting.
        string[] args =
        [
            "-y",
            "-i", options.InputPath,
            "-c:v", "hap",
            "-format", hapVariant,
            "-chunks", options.ChunkCount.ToString(CultureInfo.InvariantCulture),
            "-c:a", "copy",
            options.OutputPath,
        ];

        using var proc = StartProcess(ffmpegPath, args);
        proc.EnableRaisingEvents = true;

        var stderrBuf = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            stderrBuf.AppendLine(e.Data);

            // FFmpeg writes progress to stderr as "frame=N fps=F time=HH:MM:SS.cc ..."
            var timeMatch = TimeRx.Match(e.Data);
            if (!timeMatch.Success || totalSeconds <= 0) return;

            var currentSec = ParseTimecode(timeMatch);
            var pct = Math.Min(currentSec / totalSeconds * 100.0, 100.0);

            var frameMatch = FrameRx.Match(e.Data);
            var fpsMatch = FpsRx.Match(e.Data);

            var elapsed = stopwatch.Elapsed;
            TimeSpan? eta = pct > 0
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / (pct / 100.0)) - elapsed
                : null;

            // TryParse, never Parse: this runs on the Process's background thread, where an unhandled parse
            // exception (e.g. an absurd frame count) would terminate the whole app.
            var frames = frameMatch.Success && int.TryParse(frameMatch.Groups[1].Value, out var f) ? f : 0;
            var fps = fpsMatch.Success && double.TryParse(fpsMatch.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

            progress?.Report(new ConversionProgress
            {
                PercentComplete = pct,
                Elapsed = elapsed,
                EstimatedRemaining = eta,
                FramesProcessed = frames,
                CurrentFps = fps,
            });
        };

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();

            // Hook cancellation: kill the process so WaitForExitAsync returns promptly.
            using var cancelReg = cancellationToken.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            });

            await proc.WaitForExitAsync(cancellationToken);

            stopwatch.Stop();

            if (cancellationToken.IsCancellationRequested)
            {
                DeletePartialOutput(options.OutputPath);
                return Fail("Conversion was cancelled.", stopwatch.Elapsed);
            }

            if (proc.ExitCode != 0)
            {
                return Fail(
                    $"FFmpeg exited with code {proc.ExitCode}.\n{stderrBuf}",
                    stopwatch.Elapsed);
            }

            var outputSize = File.Exists(options.OutputPath)
                ? new FileInfo(options.OutputPath).Length : 0L;

            // Signal 100 % so the UI progress bar reaches the end.
            progress?.Report(new ConversionProgress
            {
                PercentComplete = 100,
                Elapsed = stopwatch.Elapsed,
                EstimatedRemaining = TimeSpan.Zero,
            });

            return new ConversionResult
            {
                Success = true,
                OutputPath = options.OutputPath,
                Duration = stopwatch.Elapsed,
                OutputFileSizeBytes = outputSize,
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            DeletePartialOutput(options.OutputPath);
            return Fail("Conversion was cancelled.", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Fail($"Conversion failed: {ex.Message}", stopwatch.Elapsed);
        }
    }

    // ── Metadata ───────────────────────────────────────────────────────────────

    public async Task<VideoMetadata?> GetVideoMetadataAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        // Calling GetFfmpegPath() populates _ffprobePath as a side-effect if not already set.
        if (_ffmpegPath is null) GetFfmpegPath();

        // Prefer ffprobe — its JSON output is more reliable than parsing "ffmpeg -i" stderr.
        if (_ffprobePath is not null)
        {
            var json = await RunReadAllStdout(_ffprobePath,
                "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", filePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var meta = ParseFfprobeJson(json, filePath);
                if (meta is not null) return meta;
            }
        }

        // Fall back to parsing "ffmpeg -i" stderr (ffmpeg writes stream info there and exits 1).
        return await GetMetadataViaSterrAsync(filePath);
    }

    public async Task<bool> IsHapVideoAsync(string filePath)
    {
        var meta = await GetVideoMetadataAsync(filePath);
        return meta?.Codec?.Contains("hap", StringComparison.OrdinalIgnoreCase) == true;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>Builds a child-process invocation passing each argument as a DISCRETE element via
    /// <c>ArgumentList</c> — never a single concatenated, hand-quoted string — so a file path containing a
    /// quote or space can't break out of its argument and inject ffmpeg flags (the runtime quotes each
    /// element correctly, with no shell).</summary>
    private static Process StartProcess(string exe, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            startInfo.ArgumentList.Add(a);
        return new Process { StartInfo = startInfo };
    }

    private async Task<string?> RunReadAllStdout(string exe, params string[] args)
    {
        try
        {
            using var proc = StartProcess(exe, args);
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return output;
        }
        catch
        {
            return null;
        }
    }

    private async Task<VideoMetadata?> GetMetadataViaSterrAsync(string filePath)
    {
        var ffmpegPath = GetFfmpegPath();
        if (ffmpegPath is null) return null;

        try
        {
            // "ffmpeg -i <file>" exits with code 1 but writes stream info to stderr — that's what we parse.
            using var proc = StartProcess(ffmpegPath, "-i", filePath);
            proc.Start();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var durMatch = DurationRx.Match(stderr);
            var duration = durMatch.Success
                ? TimeSpan.FromSeconds(ParseTimecode(durMatch))
                : TimeSpan.Zero;

            // TryParse, never Parse: the [\d.]+ regexes can match a malformed token (a lone "." or "1.2.3"),
            // and one bad field shouldn't discard the whole (best-effort) metadata — degrade per field instead.
            var resMatch = ResRx.Match(stderr);
            var width = resMatch.Success && int.TryParse(resMatch.Groups[1].Value, out var w) ? w : 0;
            var height = resMatch.Success && int.TryParse(resMatch.Groups[2].Value, out var h) ? h : 0;

            var frMatch = FrateRx.Match(stderr);
            var fps = frMatch.Success
                && double.TryParse(frMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fr)
                ? fr : 30.0;

            var codecMatch = CodecRx.Match(stderr);
            var codec = codecMatch.Success ? codecMatch.Groups[1].Value : "unknown";

            return new VideoMetadata
            {
                Duration = duration,
                Width = width,
                Height = height,
                FrameRate = fps,
                Codec = codec,
                FileSizeBytes = new FileInfo(filePath).Length,
            };
        }
        catch (Exception ex)
        {
            // Metadata is best-effort (it only drives the progress %), so degrade to null — but don't go
            // fully silent: surface it where a console exists (harness / redirected stdout).
            Console.Error.WriteLine($"[WARN ] HAP convert: metadata via ffmpeg stderr failed: {ex.Message}");
            return null;
        }
    }

    private static VideoMetadata? ParseFfprobeJson(string json, string filePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Duration lives in the format object.
            var durationSec = 0.0;
            if (root.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var durEl))
            {
                _ = double.TryParse(durEl.GetString(), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out durationSec);
            }

            var width = 0; var height = 0; var fps = 30.0;
            string? codec = null; string? pixFmt = null;

            // Walk streams; pick the first video stream.
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    if (!s.TryGetProperty("codec_type", out var ct) ||
                        ct.GetString() != "video") continue;

                    if (s.TryGetProperty("codec_name", out var cn)) codec = cn.GetString();
                    if (s.TryGetProperty("width", out var w)) width = w.GetInt32();
                    if (s.TryGetProperty("height", out var h)) height = h.GetInt32();
                    if (s.TryGetProperty("pix_fmt", out var pf)) pixFmt = pf.GetString();

                    // r_frame_rate is a rational string like "30000/1001".
                    if (s.TryGetProperty("r_frame_rate", out var rfr))
                    {
                        var parts = rfr.GetString()?.Split('/');
                        if (parts?.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
                            den > 0)
                        {
                            fps = num / den;
                        }
                    }

                    break; // first video stream is enough
                }
            }

            return new VideoMetadata
            {
                Duration = TimeSpan.FromSeconds(durationSec),
                Width = width,
                Height = height,
                FrameRate = fps,
                Codec = codec,
                PixelFormat = pixFmt,
                FileSizeBytes = new FileInfo(filePath).Length,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN ] HAP convert: ffprobe JSON parse failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parse an HH:MM:SS.cc timecode group from a regex match into total seconds.</summary>
    private static double ParseTimecode(Match m) =>
        int.Parse(m.Groups[1].Value) * 3600.0
        + int.Parse(m.Groups[2].Value) * 60.0
        + int.Parse(m.Groups[3].Value)
        + int.Parse(m.Groups[4].Value) / 100.0;

    private static void DeletePartialOutput(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static ConversionResult Fail(string message, TimeSpan duration) => new()
    {
        Success = false,
        ErrorMessage = message,
        Duration = duration,
    };
}
