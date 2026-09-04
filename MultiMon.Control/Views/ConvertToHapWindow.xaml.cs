using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using Microsoft.Win32;
using MultiMon.Core.Diagnostics;
using MultiMon.Control.ViewModels;
using MultiMon.Platform.Conversion;

namespace MultiMon.Control.Views;

/// <summary>
/// Single-file HAP conversion dialog. Owns no business logic — delegates everything to
/// <see cref="ConvertToHapViewModel"/>. File pickers live here (code-behind), keeping the view-model
/// platform-agnostic (no WPF reference needed there). Pattern matches <see cref="MainWindow"/>.
/// </summary>
public partial class ConvertToHapWindow : Window
{
    private const string VideoFilter =
        "Video (*.mp4;*.mov;*.mkv;*.avi;*.wmv)|*.mp4;*.mov;*.mkv;*.avi;*.wmv|All files (*.*)|*.*";

    private readonly ConvertToHapViewModel _vm;

    // Set to true while the view-model is pushing an output-path update to the TextBox; prevents the
    // TextChanged handler from marking the path as "user-edited" on programmatic changes.
    private bool _suppressOutputChanged;

    public ConvertToHapWindow(ILog log)
    {
        InitializeComponent();
        _vm = new ConvertToHapViewModel(new FfmpegHapConverter(log));
        DataContext = _vm;

        SourceInitialized += (_, _) => Theme.DarkTitleBar.Apply(new WindowInteropHelper(this).Handle);
        // Closing mid-conversion cancels it (the converter kills ffmpeg on cancellation) so the process is
        // never orphaned behind a closed dialog. No-op when nothing is running.
        Closing += (_, _) => _vm.Cancel();

        // When the view-model updates OutputPath (default-path recompute), keep _suppressOutputChanged
        // set so the TextChanged handler doesn't flag it as user-edited.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConvertToHapViewModel.OutputPath))
            {
                _suppressOutputChanged = true;
                // WPF binding updates the TextBox synchronously on the same call; clear the flag after.
                Dispatcher.InvokeAsync(() => _suppressOutputChanged = false);
            }
        };
    }

    // ── Input browse ───────────────────────────────────────────────────────────

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = VideoFilter, Title = "Select source video" };
        if (dialog.ShowDialog(this) == true)
            _vm.InputPath = dialog.FileName;
    }

    // ── Output TextBox tracking ────────────────────────────────────────────────

    /// <summary>
    /// Tells the view-model the user has manually edited the output path so it won't reset to the
    /// auto-computed default when the input path changes.
    /// </summary>
    private void OutputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_suppressOutputChanged)
            _vm.NotifyOutputPathUserEdited();
    }

    // ── Output browse ──────────────────────────────────────────────────────────

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "QuickTime movie (*.mov)|*.mov",
            DefaultExt = ".mov",
            FileName = System.IO.Path.GetFileName(_vm.OutputPath),
            InitialDirectory = System.IO.Path.GetDirectoryName(_vm.OutputPath),
            Title = "Output HAP file",
        };

        if (dialog.ShowDialog(this) == true)
        {
            _vm.NotifyOutputPathUserEdited();
            _vm.OutputPath = dialog.FileName;
        }
    }

    // ── Convert / Cancel ───────────────────────────────────────────────────────

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app, so backstop it here (ConvertAsync has its
        // own try/catch, but a cancellation/race could still surface).
        try { await _vm.ConvertAsync(); }
        catch (Exception ex) { _vm.ReportUnexpectedError(ex); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => _vm.Cancel();
}

// ── Value converter ────────────────────────────────────────────────────────────

/// <summary>
/// Converts a <see cref="HapFormat"/> enum value to a human-readable description string for the
/// ComboBox item template. Used exclusively in <see cref="ConvertToHapWindow"/>'s XAML resources.
/// </summary>
[ValueConversion(typeof(HapFormat), typeof(string))]
public sealed class HapFormatDescConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is HapFormat fmt ? Describe(fmt) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string Describe(HapFormat f) => f switch
    {
        HapFormat.Hap       => "Hap — good quality, smaller files",
        HapFormat.HapAlpha  => "Hap Alpha — with alpha channel",
        HapFormat.HapQ      => "Hap Q — best quality (recommended)",
        HapFormat.HapQAlpha => "Hap Q Alpha — best quality with alpha",
        _ => f.ToString(),
    };
}
