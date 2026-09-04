using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using MultiMon.Control.ViewModels;
using MultiMon.Control.Views;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
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

    private readonly ILog _log;

    public MainWindow(MainViewModel viewModel, ILog log)
    {
        InitializeComponent();
        _vm = viewModel;
        _log = log;
        DataContext = viewModel;

        GpuText.Text = $"GPU: {GpuCapabilityService.DetectedGpuName}   ·   " +
                       $"Decode: {(GpuCapabilityService.PreferSoftwareDecode ? "software (GPU on the compatibility list)" : "hardware")}   ·   " +
                       $"HAP: {(GpuCapabilityService.SupportsHap ? "enabled" : "disabled")}";

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
        => new ConvertToHapWindow(_log) { Owner = this }.ShowDialog();

    /// <summary>Open the MultiMon issue tracker, pre-filling the bug form with real runtime diagnostics
    /// (version, GPU, monitors, OS/.NET, and a recent log excerpt) so reports carry accurate data instead
    /// of user guesses. Pure shell-launch — no controller/native involvement (the firewall, ADR 0003 D4,
    /// is unaffected). Diagnostics gathering is best-effort: any failure falls back to the plain form URL.</summary>
    private void ReportBug_Click(object sender, RoutedEventArgs e)
    {
        string url;
        try { url = BuildBugReportUrl(); }
        catch { url = IssueFormBase; }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open the browser. Please report bugs at:\nhttps://github.com/Fiavaion/MultiMon/issues\n\n{ex.Message}",
                "Report a bug", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private const string IssueFormBase = "https://github.com/Fiavaion/MultiMon/issues/new?template=bug_report.yml";
    private const int MaxIssueUrlLength = 6500; // keep well under browser/GitHub URL limits

    /// <summary>Builds the GitHub issue-form URL with prefilled fields. Each query key matches a field id
    /// in .github/ISSUE_TEMPLATE/bug_report.yml (version, gpu_vendor, monitors, logs).</summary>
    private string BuildBugReportUrl()
    {
        var version = ResolveVersion();
        var gpuName = GpuCapabilityService.DetectedGpuName;
        var vendor = GpuCapabilityService.DetectedVendor;
        var hap = GpuCapabilityService.SupportsHap ? "enabled" : "disabled";
        var monitors = DescribeMonitors();

        var diagnostics =
            "Auto-collected diagnostics (please keep this):\n" +
            $"MultiMon version: {version}\n" +
            $"OS: {RuntimeInformation.OSDescription}\n" +
            $".NET: {RuntimeInformation.FrameworkDescription}\n" +
            $"GPU: {gpuName} (vendor={vendor}, HAP={hap})\n" +
            $"Monitors: {monitors}\n\n" +
            "--- Recent log ---\n" + ReadLogExcerpt();

        string Build(string logs) => IssueFormBase
            + "&version=" + Uri.EscapeDataString(version)
            + "&gpu_vendor=" + Uri.EscapeDataString(MapGpuVendorOption(vendor, gpuName))
            + "&monitors=" + Uri.EscapeDataString(monitors)
            + "&logs=" + Uri.EscapeDataString(logs);

        var url = Build(diagnostics);
        if (url.Length > MaxIssueUrlLength)
        {
            var overflow = url.Length - MaxIssueUrlLength;
            var keep = Math.Max(0, diagnostics.Length - overflow - 16);
            url = Build(diagnostics[..keep] + "\n...(truncated)");
        }
        return url;
    }

    private static string ResolveVersion()
    {
        var asm = typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+'); // strip the +<commit-sha> SourceLink suffix
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "unknown";
    }

    /// <summary>Maps the detected vendor to the exact dropdown option label in the issue form. The precise
    /// GPU name still goes in the diagnostics block, so a mis-bucketed dropdown is never the only signal.</summary>
    private static string MapGpuVendorOption(GpuVendor vendor, string gpuName) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase)
            ? "Intel (discrete / Arc)" : "Intel (integrated / iGPU)",
        _ => "Other / not sure",
    };

    private string DescribeMonitors()
    {
        var list = _vm.Monitors
            .Select(m => $"{m.Resolution}@{m.RefreshRate:0}Hz{(m.IsPrimary ? " (primary)" : "")}")
            .ToList();
        return list.Count == 0 ? "none detected" : $"{list.Count} — {string.Join(", ", list)}";
    }

    /// <summary>Reads the newest MultiMon log and returns the startup banner + any error lines (crashes are
    /// logged as "[ERROR] CRASH:") + the tail, PII-scrubbed and capped in length — this text lands in a
    /// PUBLIC GitHub issue URL. Best-effort — never throws into the caller.</summary>
    private static string ReadLogExcerpt()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiMon", "Logs");
            var newest = new DirectoryInfo(dir).GetFiles("multimon-*.log")
                .OrderByDescending(f => f.Name).FirstOrDefault();
            if (newest is null) return "(no log file found)";

            var lines = File.ReadAllLines(newest.FullName)
                .Where(l => !l.Contains("machine=") && !l.Contains("appDir=")) // machine name / install path
                .Select(ScrubLine)
                .ToArray();
            var banner = lines.Where(l =>
                l.Contains("Env:") || l.Contains("Gpu:") || l.Contains("Monitor") || l.Contains("feature level"))
                .Take(14);
            var errors = lines.Where(l => l.Contains("[ERROR]")).Reverse().Take(8).Reverse();
            var tail = lines.Reverse().Take(10).Reverse();
            var text = string.Join("\n", banner.Concat(errors).Concat(tail).Distinct());
            return text.Length > 2500 ? text[..2500] + "\n...(truncated)" : text;
        }
        catch (Exception ex)
        {
            return $"(could not read log: {ex.Message})";
        }
    }

    // A quoted absolute Windows path, as the controller logs clip paths ('D:\clips\show.mp4').
    private static readonly Regex QuotedPath = new(@"'([A-Za-z]:\\[^']+)'", RegexOptions.Compiled);

    /// <summary>Drops the user's profile path and account name (as a path segment, so a short name can't
    /// mangle ordinary words); every quoted clip path keeps only its file name (the directory tree can
    /// identify a person or a client, and the controller logs clip paths at INFO as well as ERROR).</summary>
    private static string ScrubLine(string line)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            line = line.Replace(profile, "<user>", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (!string.IsNullOrEmpty(user))
            line = Regex.Replace(line, @"(?<=\\)" + Regex.Escape(user) + @"(?=\\|$|\s)", "<user>", RegexOptions.IgnoreCase);
        line = QuotedPath.Replace(line, m => $"'{Path.GetFileName(m.Groups[1].Value)}'");
        return line;
    }
}
