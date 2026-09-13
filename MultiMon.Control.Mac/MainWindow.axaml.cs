using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Metal;
using MultiMon.Control.Shared;
using MultiMon.Core.Diagnostics;

namespace MultiMon.Control.Mac;

/// <summary>
/// Thin control-panel window (no embedded video — the V0087 airspace failure cannot recur). It binds to
/// <see cref="MainViewModel"/>, which talks to the pipeline only through <c>IPerformanceController</c>;
/// this window never references a Metal output, a decoder or the render loop (the firewall, ADR 0003 D4).
/// Handlers forward to the view-model and own the file pickers.
///
/// <para>Transport keys mirror the Windows panel's semantics: F11 toggles perform for the whole session,
/// Esc (exit) and Space (pause/resume) act only while performing, so both type normally while you are
/// configuring. Windows takes them as global hotkeys because perform MINIMIZES the panel; on macOS the
/// output windows sit above the menu bar and ignore mouse events, so the panel stays the key window and
/// ordinary window key handling is enough — no accessibility permission, no global event monitor.</para>
/// </summary>
public partial class MainWindow : Window
{
    private static readonly FilePickerFileType ProjectType =
        new("MultiMon project") { Patterns = ["*.mmproj"] };
    private static readonly FilePickerFileType VideoType =
        new("Video") { Patterns = ["*.mp4", "*.mov", "*.mkv", "*.avi"] };
    private static readonly FilePickerFileType AudioType =
        new("Audio / video") { Patterns = ["*.mp3", "*.wav", "*.flac", "*.aac", "*.m4a", "*.mp4", "*.mov"] };

    private readonly MainViewModel _vm;
    private readonly ILog _log;
    private readonly IdentifyOverlays _identify = new();

    /// <summary><paramref name="matchDisplayRefresh"/> seeds the "Match display refresh rate" box and
    /// <paramref name="onMatchDisplayRefreshChanged"/> receives every toggle — an app-level option (not project
    /// content), so it is wired by the composition root rather than the shared view-model.</summary>
    public MainWindow(MainViewModel viewModel, ILog log, bool matchDisplayRefresh, Action<bool> onMatchDisplayRefreshChanged)
    {
        InitializeComponent();
        _vm = viewModel;
        _log = log;
        DataContext = viewModel;

        var matchRefresh = this.FindControl<CheckBox>("MatchRefreshCheck")!;
        matchRefresh.IsChecked = matchDisplayRefresh;
        matchRefresh.IsCheckedChanged += (_, _) => onMatchDisplayRefreshChanged(matchRefresh.IsChecked == true);

        this.FindControl<TextBlock>("GpuText")!.Text =
            $"GPU: {MTLDevice.SystemDefault?.Name ?? "(no Metal device)"}   ·   " +
            "Decode: VideoToolbox (hardware, automatic software fallback)   ·   HAP: enabled";

        _vm.PropertyChanged += OnViewModelChanged;
        KeyDown += OnKeyDown;
        Closed += (_, _) => _identify.Hide();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>A show is starting — get the number overlays off the output monitors.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPerforming) && _vm.IsPerforming)
            _identify.Hide();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                _vm.TogglePerform();
                e.Handled = true;
                break;
            // Esc and Space only bite while performing, so they type normally during configuration.
            case Key.Escape when _vm.IsPerforming:
                _vm.Stop();
                e.Handled = true;
                break;
            // The identify overlays never take focus (ShowActivated = false), so this is the keyboard's
            // way to dismiss them.
            case Key.Escape when _identify.Active:
                _identify.Hide();
                e.Handled = true;
                break;
            case Key.Space when _vm.IsPerforming:
                _vm.TogglePause();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Toggle the number overlay on every monitor (helps map physical screens to the per-monitor
    /// assignment rows). Click any overlay, press Esc, or this button again, to dismiss.</summary>
    private void Identify_Click(object? sender, RoutedEventArgs e) => _identify.Toggle(_vm.Monitors);

    private async void BrowseRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Control { DataContext: MonitorAssignmentRow row })
            return;
        var path = await PickOpenAsync($"Video for {row.DisplayName}", VideoType);
        if (path is not null)
            row.FilePath = path;
    }

    private async void BrowseSpan_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenAsync("Source video (shown across all monitors)", VideoType);
        if (path is not null)
            _vm.SpanSourcePath = path;
    }

    private void Perform_Click(object? sender, RoutedEventArgs e) => _vm.Perform();
    private void Stop_Click(object? sender, RoutedEventArgs e) => _vm.Stop();

    private async void AddAudio_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenAsync("Add audio track", AudioType);
        if (path is not null)
            _vm.AddAudioTrack(path);
    }

    private void RemoveAudio_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Avalonia.Controls.Control { DataContext: AudioTrackRow row })
            _vm.RemoveAudioTrack(row);
    }

    private async void New_Click(object? sender, RoutedEventArgs e)
    {
        // Confirm before discarding work in progress (a blank project has nothing to lose).
        if (_vm.HasContent &&
            !await ConfirmWindow.AskAsync(this, "New project", "Discard the current project and start a new one?"))
            return;
        _vm.New();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = "show.mmproj",
            DefaultExtension = "mmproj",
            FileTypeChoices = [ProjectType],
        });
        if (LocalPath(file) is { } path)
            _vm.Save(path);
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        var path = await PickOpenAsync("Open project", ProjectType);
        if (path is not null)
            _vm.Load(path);
    }

    private void ConvertHap_Click(object? sender, RoutedEventArgs e)
        => new ConvertToHapWindow(_log).ShowDialog(this);

    private async Task<string?> PickOpenAsync(string title, FilePickerFileType type)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [type],
        });
        return LocalPath(files.Count > 0 ? files[0] : null);
    }

    /// <summary>The pipeline takes file-system paths; a picker can hand back a non-file location
    /// (a cloud provider), which has no local path — treat that as "nothing chosen".</summary>
    private static string? LocalPath(IStorageItem? item) => item?.TryGetLocalPath();
}
