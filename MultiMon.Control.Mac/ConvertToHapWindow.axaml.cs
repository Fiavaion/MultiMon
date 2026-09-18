using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MultiMon.Control.Shared;
using MultiMon.Control.Shared.Conversion;
using MultiMon.Core.Diagnostics;

namespace MultiMon.Control.Mac;

/// <summary>
/// Single-file HAP conversion dialog. Owns no business logic — everything is in the shared
/// <see cref="ConvertToHapViewModel"/>; only the file pickers live here. Mirrors the Windows dialog
/// field for field (ffmpeg status, input + metadata, format + chunks, output, progress, Convert/Cancel).
/// </summary>
public partial class ConvertToHapWindow : Window
{
    private static readonly FilePickerFileType VideoType =
        new("Video") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi", "*.wmv"] };
    private static readonly FilePickerFileType MovType =
        new("QuickTime movie") { Patterns = ["*.mov"] };

    private readonly ConvertToHapViewModel _vm;

    // Set while the view-model is pushing an output-path update into the TextBox, so the TextChanged
    // handler doesn't mark a programmatic default-path recompute as "user-edited".
    private bool _suppressOutputChanged;

    public ConvertToHapWindow(ILog log)
    {
        InitializeComponent();
        _vm = new ConvertToHapViewModel(new FfmpegHapConverter(log));
        DataContext = _vm;

        // Bare enum items (so SelectedFormat binds directly); the template renders the description.
        this.FindControl<ComboBox>("FormatCombo")!.ItemsSource = ConvertToHapViewModel.AvailableFormats;

        // Closing mid-conversion cancels it (the converter kills ffmpeg on cancellation) so the process is
        // never orphaned behind a closed dialog. No-op when nothing is running.
        Closing += (_, _) => _vm.Cancel();

        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ConvertToHapViewModel.OutputPath))
                return;
            _suppressOutputChanged = true;
            // The binding updates the TextBox on this same turn; clear the flag on the next one.
            Dispatcher.UIThread.Post(() => _suppressOutputChanged = false);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void BrowseInput_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select source video",
            AllowMultiple = false,
            FileTypeFilter = [VideoType],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            _vm.InputPath = path;
    }

    /// <summary>Tells the view-model the user has manually edited the output path, so it won't reset to
    /// the auto-computed default when the input path changes.</summary>
    private void OutputBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_suppressOutputChanged)
            _vm.NotifyOutputPathUserEdited();
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(_vm.OutputPath);
        var options = new FilePickerSaveOptions
        {
            Title = "Output HAP file",
            SuggestedFileName = Path.GetFileName(_vm.OutputPath),
            DefaultExtension = "mov",
            FileTypeChoices = [MovType],
        };
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(dir);

        var file = await StorageProvider.SaveFilePickerAsync(options);
        if (file?.TryGetLocalPath() is { } path)
        {
            _vm.NotifyOutputPathUserEdited();
            _vm.OutputPath = path;
        }
    }

    private async void Convert_Click(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app, so backstop it here (ConvertAsync has its
        // own try/catch, but a cancellation/race could still surface).
        try { await _vm.ConvertAsync(); }
        catch (Exception ex) { _vm.ReportUnexpectedError(ex); }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => _vm.Cancel();
}

/// <summary>Renders a <see cref="HapFormat"/> as its one-line description in the format combo — the same
/// four strings the Windows dialog shows. Used only by <see cref="ConvertToHapWindow"/>'s XAML.</summary>
public sealed class HapFormatDescConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        HapFormat.Hap => "Hap — good quality, smaller files",
        HapFormat.HapAlpha => "Hap Alpha — with alpha channel",
        HapFormat.HapQ => "Hap Q — best quality (recommended)",
        HapFormat.HapQAlpha => "Hap Q Alpha — best quality with alpha",
        HapFormat f => f.ToString(),
        _ => string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
