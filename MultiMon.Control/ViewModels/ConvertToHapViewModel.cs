using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MultiMon.Platform.Conversion;

namespace MultiMon.Control.ViewModels;

/// <summary>
/// View-model for the single-file HAP conversion dialog. Owns the <see cref="FfmpegHapConverter"/>
/// instance and all UI state. Manual <see cref="INotifyPropertyChanged"/> — no MVVM toolkit.
///
/// <para>The conversion runs on a thread-pool task via <c>await</c>; progress callbacks marshal back to
/// the UI thread via <see cref="Application.Current.Dispatcher"/>. No blocking calls on the UI thread.</para>
/// </summary>
public sealed class ConvertToHapViewModel : INotifyPropertyChanged
{
    private readonly IVideoConverter _converter;
    private CancellationTokenSource? _cts;

    private string _ffmpegStatus = "Checking FFmpeg…";
    private bool _ffmpegFound;

    private string _inputPath = string.Empty;
    private string _metadataText = string.Empty;

    private HapFormat _selectedFormat = HapFormat.HapQ;
    private int _chunkCount = 4;

    private string _outputPath = string.Empty;
    private bool _outputPathUserEdited;  // true once the user has manually changed the output path

    private bool _isConverting;
    private double _progress;
    private string _statusText = string.Empty;

    public ConvertToHapViewModel(IVideoConverter converter)
    {
        _converter = converter;
        _ = CheckFfmpegAsync();
    }

    // ── FFmpeg availability ────────────────────────────────────────────────────

    /// <summary>
    /// "FFmpeg found: &lt;path&gt;" or "FFmpeg not found — …" shown at the top of the dialog.
    /// Drives the Convert button's enabled state.
    /// </summary>
    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        private set { _ffmpegStatus = value; OnChanged(); }
    }

    public bool FfmpegFound
    {
        get => _ffmpegFound;
        private set { _ffmpegFound = value; OnChanged(); OnChanged(nameof(CanConvert)); }
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    /// <summary>Path chosen by the user via the Browse button or typed directly.</summary>
    public string InputPath
    {
        get => _inputPath;
        set
        {
            _inputPath = value;
            OnChanged();
            OnChanged(nameof(CanConvert));
            // Recompute the default output path unless the user already customised it.
            if (!_outputPathUserEdited)
                OutputPath = BuildDefaultOutputPath(value);
            _ = LoadMetadataAsync(value);
        }
    }

    /// <summary>One-line metadata summary shown below the input picker: "1920×1080 · 15.0s · h264 · 38.1 MB".</summary>
    public string MetadataText
    {
        get => _metadataText;
        private set { _metadataText = value; OnChanged(); }
    }

    // ── Encode settings ────────────────────────────────────────────────────────

    public static HapFormat[] AvailableFormats { get; } =
    [
        HapFormat.Hap,
        HapFormat.HapAlpha,
        HapFormat.HapQ,
        HapFormat.HapQAlpha,
    ];

    public HapFormat SelectedFormat
    {
        get => _selectedFormat;
        set { _selectedFormat = value; OnChanged(); }
    }

    /// <summary>Chunk count (1–16) passed to ffmpeg's -chunks argument.</summary>
    public int ChunkCount
    {
        get => _chunkCount;
        set { _chunkCount = Math.Clamp(value, 1, 16); OnChanged(); }
    }

    // ── Output ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Destination .mov path. Defaults to &lt;input dir&gt;\&lt;input name&gt;_hap.mov; the user can
    /// override it via the Save button or by editing the TextBox. Once the user has edited it, automatic
    /// recomputation on input-path change is suppressed.
    /// </summary>
    public string OutputPath
    {
        get => _outputPath;
        set { _outputPath = value; OnChanged(); OnChanged(nameof(CanConvert)); }
    }

    /// <summary>Called from the code-behind when the user edits the output TextBox directly.</summary>
    public void NotifyOutputPathUserEdited() => _outputPathUserEdited = true;

    // ── Conversion state ───────────────────────────────────────────────────────

    public bool IsConverting
    {
        get => _isConverting;
        private set { _isConverting = value; OnChanged(); OnChanged(nameof(CanConvert)); OnChanged(nameof(CanCancel)); OnChanged(nameof(CanEdit)); }
    }

    /// <summary>Inputs (file pickers, format, chunks, output) are editable except while a conversion
    /// runs. NOT gated on <see cref="CanConvert"/> — that needs an input+output already chosen, which
    /// would lock the very pickers used to choose them.</summary>
    public bool CanEdit => !_isConverting;

    /// <summary>Progress bar value 0–100.</summary>
    public double Progress
    {
        get => _progress;
        private set { _progress = value; OnChanged(); }
    }

    /// <summary>Status line shown below the progress bar.</summary>
    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnChanged(); }
    }

    public bool CanConvert =>
        _ffmpegFound
        && !string.IsNullOrWhiteSpace(_inputPath)
        && !string.IsNullOrWhiteSpace(_outputPath)
        && !_isConverting;

    public bool CanCancel => _isConverting;

    // ── Commands (called from code-behind) ─────────────────────────────────────

    public async Task ConvertAsync()
    {
        if (!CanConvert) return;

        _cts = new CancellationTokenSource();
        IsConverting = true;
        Progress = 0;
        StatusText = "Starting…";

        var options = new ConversionOptions
        {
            InputPath = _inputPath,
            OutputPath = _outputPath,
            Format = _selectedFormat,
            ChunkCount = _chunkCount,
        };

        var progressHandler = new Progress<ConversionProgress>(p =>
        {
            // Already on the UI thread because Progress<T> captures the synchronization context.
            Progress = p.PercentComplete;

            StatusText = p.EstimatedRemaining.HasValue
                ? $"Converting… {p.PercentComplete:F1}% (ETA {p.EstimatedRemaining.Value:mm\\:ss})"
                : $"Converting… {p.PercentComplete:F1}%";
        });

        ConversionResult result;
        try
        {
            result = await _converter.ConvertToHapAsync(options, progressHandler, _cts.Token);
        }
        catch (Exception ex)
        {
            result = new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsConverting = false;
        }

        if (result.Success)
        {
            Progress = 100;
            StatusText = $"Done — {FormatBytes(result.OutputFileSizeBytes)}";
        }
        else
        {
            StatusText = $"Error: {result.ErrorMessage}";
        }
    }

    public void Cancel()
    {
        if (!CanCancel) return;
        StatusText = "Cancelling…";
        _cts?.Cancel();
    }

    /// <summary>Surface an unexpected exception (the async-void backstop in the code-behind) without crashing.</summary>
    public void ReportUnexpectedError(Exception ex)
    {
        IsConverting = false;
        StatusText = $"Unexpected error: {ex.Message}";
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private async Task CheckFfmpegAsync()
    {
        // Fire-and-forget from the ctor (`_ = CheckFfmpegAsync()`): a faulted probe must NOT leave the
        // dialog stuck on "Checking FFmpeg…". Catch, surface the failure, and keep Convert disabled.
        try
        {
            var found = await _converter.IsAvailableAsync();
            var path = _converter.GetFfmpegPath();

            FfmpegFound = found;
            FfmpegStatus = found
                ? $"FFmpeg found: {path}"
                : "FFmpeg not found — install it or place ffmpeg.exe in an 'ffmpeg' folder next to the app.";
        }
        catch (Exception ex)
        {
            FfmpegFound = false;
            FfmpegStatus = $"FFmpeg check failed: {ex.Message}";
        }
    }

    private async Task LoadMetadataAsync(string path)
    {
        MetadataText = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        // Fire-and-forget from the InputPath setter; metadata is cosmetic, so a fault degrades to a note
        // instead of faulting the task silently.
        try
        {
            var meta = await _converter.GetVideoMetadataAsync(path);
            if (meta is null) return;

            var size = FormatBytes(meta.FileSizeBytes);
            MetadataText = $"{meta.Width}×{meta.Height} · {meta.Duration.TotalSeconds:F1}s · {meta.Codec ?? "?"} · {size}";
        }
        catch (Exception)
        {
            MetadataText = "(metadata unavailable)";
        }
    }

    private static string BuildDefaultOutputPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return string.Empty;
        var dir = Path.GetDirectoryName(inputPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(inputPath) + "_hap.mov";
        return Path.Combine(dir, name);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
