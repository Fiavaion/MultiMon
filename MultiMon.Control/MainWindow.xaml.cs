using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using MultiMon.Control.ViewModels;
using MultiMon.Control.Views;
using MultiMon.Platform;

namespace MultiMon.Control;

/// <summary>
/// Thin control-panel window (no embedded video — the V0087 airspace failure cannot recur). It binds to
/// <see cref="MainViewModel"/>, which talks to the pipeline only through <c>IPerformanceController</c>;
/// this window never references a D3D11 / decode type (the firewall, ADR 0003 D4). Button handlers just
/// forward to the view-model and own the file dialogs.
///
/// <para>Transport shortcuts are GLOBAL hotkeys (RegisterHotKey), not window key events, so they keep
/// working after perform mode MINIMIZES this panel to get it off the output monitors. F11 (toggle perform)
/// is registered for the whole session; Space (pause/resume) and Esc (exit perform) only while performing,
/// so those keys type normally while you're configuring.</para>
/// </summary>
public partial class MainWindow : Window
{
    private const string ProjectFilter = "MultiMon project (*.mmproj)|*.mmproj|All files (*.*)|*.*";
    private const string VideoFilter = "Video (*.mp4;*.mov;*.mkv;*.avi)|*.mp4;*.mov;*.mkv;*.avi|All files (*.*)|*.*";
    private const string AudioFilter = "Audio/Video (*.mp3;*.wav;*.flac;*.aac;*.m4a;*.mp4;*.mov)|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.mp4;*.mov|All files (*.*)|*.*";

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NONE = 0x0000;
    private const uint VK_F11 = 0x7A;
    private const uint VK_SPACE = 0x20;
    private const uint VK_ESCAPE = 0x1B;
    private const int HotkeyPerform = 1;   // F11
    private const int HotkeyPause = 2;      // Space (registered only while performing)
    private const int HotkeyExit = 3;      // Esc (registered only while performing)

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly MainViewModel _vm;
    private readonly Views.IdentifyOverlays _identify = new();
    private IntPtr _hwnd;
    private bool _performHotkeysRegistered;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        GpuText.Text = $"GPU: {GpuCapabilityService.DetectedGpuName}   ·   " +
                       $"HAP: {(GpuCapabilityService.SupportsHap ? "supported" : "fallback to Media Foundation")}";

        _vm.PropertyChanged += OnViewModelChanged;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Theme.DarkTitleBar.Apply(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndHook);
        if (!RegisterHotKey(_hwnd, HotkeyPerform, MOD_NONE, VK_F11))
            _vm.NotifyHotkeyUnavailable("F11");
    }

    private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyPerform: _vm.TogglePerform(); handled = true; break;
                case HotkeyPause: _vm.TogglePause(); handled = true; break;
                case HotkeyExit: _vm.Stop(); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Perform mode minimizes the panel off the output monitors (and registers the Space hotkey);
    /// leaving perform restores it. Driven by the view-model's IsPerforming so F11 and the buttons share it.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsPerforming))
            return;

        if (_vm.IsPerforming)
        {
            _identify.Hide(); // a show is starting — get the number overlays off the output monitors
            WindowState = WindowState.Minimized;
            // Space = pause/resume, Esc = exit perform — registered ONLY while performing, so both keys
            // type normally while configuring. F11 stays registered for the whole session.
            if (_hwnd != IntPtr.Zero && !_performHotkeysRegistered)
            {
                // Notify on failure (as F11 does): another app owning Space/Esc would otherwise leave the
                // performer with no working pause/exit key and no warning.
                if (!RegisterHotKey(_hwnd, HotkeyPause, MOD_NONE, VK_SPACE))
                    _vm.NotifyHotkeyUnavailable("Space (pause)");
                if (!RegisterHotKey(_hwnd, HotkeyExit, MOD_NONE, VK_ESCAPE))
                    _vm.NotifyHotkeyUnavailable("Esc (exit perform)");
                _performHotkeysRegistered = true;
            }
        }
        else
        {
            if (_hwnd != IntPtr.Zero && _performHotkeysRegistered)
            {
                UnregisterHotKey(_hwnd, HotkeyPause);
                UnregisterHotKey(_hwnd, HotkeyExit);
                _performHotkeysRegistered = false;
            }
            WindowState = WindowState.Normal;
            Activate();
        }
    }

    /// <summary>Toggle the Windows-11-style number overlay on every monitor (helps map physical screens to
    /// the per-monitor assignment grid). Click any overlay, or this button again, to dismiss.</summary>
    private void Identify_Click(object sender, RoutedEventArgs e) => _identify.Toggle(_vm.Monitors);

    private void OnClosed(object? sender, EventArgs e)
    {
        _identify.Hide();
        if (_hwnd == IntPtr.Zero) return;
        if (_performHotkeysRegistered)
        {
            UnregisterHotKey(_hwnd, HotkeyPause);
            UnregisterHotKey(_hwnd, HotkeyExit);
        }
        UnregisterHotKey(_hwnd, HotkeyPerform);
    }

    private void BrowseRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitorAssignmentRow row })
            return;
        var dialog = new OpenFileDialog { Filter = VideoFilter, Title = $"Video for {row.DisplayName}" };
        if (dialog.ShowDialog(this) == true)
            row.FilePath = dialog.FileName;
    }

    private void BrowseSpan_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = VideoFilter, Title = "Source video (shown across all monitors)" };
        if (dialog.ShowDialog(this) == true)
            _vm.SpanSourcePath = dialog.FileName;
    }

    private void Perform_Click(object sender, RoutedEventArgs e) => _vm.Perform();
    private void Stop_Click(object sender, RoutedEventArgs e) => _vm.Stop();

    private void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = AudioFilter, Title = "Add audio track" };
        if (dialog.ShowDialog(this) == true)
            _vm.AddAudioTrack(dialog.FileName);
    }

    private void RemoveAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrackRow row })
            _vm.RemoveAudioTrack(row);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        // Confirm before discarding work in progress (a blank project has nothing to lose).
        if (_vm.HasContent &&
            MessageBox.Show(this, "Discard the current project and start a new one?", "New project",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _vm.New();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = ProjectFilter, DefaultExt = ".mmproj", FileName = "show.mmproj" };
        if (dialog.ShowDialog(this) == true)
            _vm.Save(dialog.FileName);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = ProjectFilter };
        if (dialog.ShowDialog(this) == true)
            _vm.Load(dialog.FileName);
    }

    private void ConvertHap_Click(object sender, RoutedEventArgs e)
        => new ConvertToHapWindow { Owner = this }.ShowDialog();

    /// <summary>Open the MultiMon issue tracker in the default browser so users can file a bug report.
    /// Pure shell-launch (no controller/native involvement — the firewall, ADR 0003 D4, is unaffected).</summary>
    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        const string url = "https://github.com/Fiavaion/MultiMon/issues/new?template=bug_report.yml";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the browser. Please report bugs at:\n{url}\n\n{ex.Message}",
                "Report a bug", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
