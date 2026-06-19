using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using MultiMon.Core;
using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Control.ViewModels;

/// <summary>
/// One assignable monitor slot in the control panel: a file path + HAP flag for a monitor.
/// </summary>
public sealed class MonitorAssignmentRow : INotifyPropertyChanged
{
    private string _filePath = string.Empty;

    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }

    public string FilePath
    {
        get => _filePath;
        set { _filePath = value; OnChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// One audio track in the mixer. Live control changes (volume/mute/solo/pan) are pushed straight to the
/// running engine through <see cref="IPerformanceController"/> so they take effect mid-performance.
/// </summary>
public sealed class AudioTrackRow : INotifyPropertyChanged
{
    private readonly IPerformanceController _controller;
    private double _volume = 1.0;
    private bool _muted;
    private bool _solo;
    private double _pan;

    public AudioTrackRow(IPerformanceController controller, string id, string name, string filePath)
    {
        _controller = controller;
        Id = id;
        Name = name;
        FilePath = filePath;
    }

    public string Id { get; }
    public string Name { get; }
    public string FilePath { get; }
    public string? OutputDeviceId { get; set; }

    /// <summary>True when this track is auto-derived from a loaded video's embedded audio. Linked tracks
    /// follow their video slot (added/updated/removed automatically) and are not independently removable;
    /// their volume/pan/mute/solo are still live-editable.</summary>
    public bool IsLinked { get; init; }

    /// <summary>A linked track can't be removed by hand (it tracks its video); manual tracks can.</summary>
    public bool CanRemove => !IsLinked;

    public double Volume
    {
        get => _volume;
        set { _volume = value; OnChanged(); _controller.SetTrackVolume(Id, value); }
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; OnChanged(); _controller.SetTrackMuted(Id, value); }
    }

    public bool Solo
    {
        get => _solo;
        set { _solo = value; OnChanged(); _controller.SetTrackSolo(Id, value); }
    }

    public double Pan
    {
        get => _pan;
        set { _pan = value; OnChanged(); _controller.SetTrackPan(Id, value); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// The control-panel view-model. It issues commands to the pipeline ONLY through
/// <see cref="IPerformanceController"/> (Core types in, Core types out) and NEVER touches a D3D11 /
/// decode type or blocks on native teardown — the architectural firewall (ADR 0003 D4). Every command
/// posts and returns; the controller does its work off the UI thread.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string SingleSlotKey = "__single_source__";

    private readonly IPerformanceController _controller;
    private readonly ILog _log;
    private ShowMode _selectedMode = ShowMode.Individual;
    private AudioOutputDevice? _selectedAudioDevice;
    private string _status = "Idle.";
    private bool _isPerforming;
    private bool _syncIndividual;
    private double _masterVolume = 1.0;
    private bool _masterMuted;
    private string _spanSourcePath = string.Empty;
    private bool _splitAuto = true;
    private int _splitRows = 2;
    private int _splitColumns = 2;

    // Auto-loaded (linked) embedded-audio tracks, keyed by video slot (monitor DeviceId, or SingleSlotKey
    // for the one-source modes). _linkedSlotPaths tracks the path each linked track was probed against so a
    // refresh only re-probes when a path actually changed. Bulk loads suppress the refresh until done.
    private readonly Dictionary<string, AudioTrackRow> _linkedTracks = new();
    private readonly Dictionary<string, string> _linkedSlotPaths = new();
    private bool _suppressLinkedRefresh;

    public MainViewModel(IPerformanceController controller, ILog log)
    {
        _controller = controller;
        _log = log;

        Rows = new ObservableCollection<MonitorAssignmentRow>(
            controller.Monitors.Select(m => new MonitorAssignmentRow { DeviceId = m.DeviceId, DisplayName = m.ToString() }));
        foreach (var row in Rows)
            row.PropertyChanged += OnAssignmentRowChanged;

        AudioDevices = new ObservableCollection<AudioOutputDevice>(controller.GetAudioDevices());
        _selectedAudioDevice = AudioDevices.FirstOrDefault(d => d.IsDefault) ?? AudioDevices.FirstOrDefault();
        AudioTracks = new ObservableCollection<AudioTrackRow>();

        _controller.StateChanged += OnControllerStateChanged;
    }

    public ObservableCollection<MonitorAssignmentRow> Rows { get; }
    public ObservableCollection<AudioTrackRow> AudioTracks { get; }
    public ObservableCollection<AudioOutputDevice> AudioDevices { get; }

    /// <summary>Master volume (0–1), applied live to the running mix.</summary>
    public double MasterVolume
    {
        get => _masterVolume;
        set { _masterVolume = value; OnChanged(); _controller.SetMasterVolume(value); }
    }

    /// <summary>Master mute, applied live.</summary>
    public bool MasterMuted
    {
        get => _masterMuted;
        set { _masterMuted = value; OnChanged(); _controller.SetMasterMuted(value); }
    }
    public IReadOnlyList<ShowMode> Modes { get; } = new[] { ShowMode.Span, ShowMode.Individual, ShowMode.Hap, ShowMode.Split };

    public ShowMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            _selectedMode = value;
            OnChanged();
            OnChanged(nameof(SyncToggleVisible));
            OnChanged(nameof(IsSingleSourceMode));
            OnChanged(nameof(IsMultiSourceMode));
            OnChanged(nameof(IsSplitMode));
            if (_selectedMode == ShowMode.Split && _splitAuto)
                ApplyAutoGrid();
            if (_suppressLinkedRefresh)
                return;
            // Entering a single-source mode with no source chosen yet: prefill from the first assigned clip
            // so the one input isn't empty after a mode flip. The SpanSourcePath setter refreshes linked audio.
            if (IsSingleSourceMode && string.IsNullOrWhiteSpace(_spanSourcePath))
            {
                var first = Rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FilePath))?.FilePath;
                if (!string.IsNullOrWhiteSpace(first)) { SpanSourcePath = first; return; }
            }
            RefreshLinkedAudio();
        }
    }

    /// <summary>The single source clip for the one-source modes (Span / Split). Bound to the single
    /// "Source video" picker that replaces the per-monitor grid in those modes.</summary>
    public string SpanSourcePath
    {
        get => _spanSourcePath;
        set
        {
            _spanSourcePath = value ?? string.Empty;
            OnChanged();
            if (!_suppressLinkedRefresh)
                RefreshLinkedAudio();
        }
    }

    /// <summary>Span / Split play ONE source across the outputs, so the UI shows a single source input.</summary>
    public bool IsSingleSourceMode => _selectedMode is ShowMode.Span or ShowMode.Split;

    /// <summary>Individual / Hap assign a clip per monitor, so the UI shows the per-monitor grid.</summary>
    public bool IsMultiSourceMode => !IsSingleSourceMode;

    /// <summary>Split mode shows the grid controls (auto toggle + rows/columns).</summary>
    public bool IsSplitMode => _selectedMode == ShowMode.Split;

    /// <summary>The physical monitors, in panel order — used by the Identify overlay.</summary>
    public IReadOnlyList<MonitorInfo> Monitors => _controller.Monitors;

    /// <summary>When true, the split grid is derived from the screen count (near-square); when false the
    /// user sets <see cref="SplitRows"/>/<see cref="SplitColumns"/> explicitly.</summary>
    public bool SplitAuto
    {
        get => _splitAuto;
        set
        {
            _splitAuto = value;
            OnChanged();
            OnChanged(nameof(SplitGridEditable));
            if (value) ApplyAutoGrid();
        }
    }

    /// <summary>Rows/columns are editable only when the auto grid is off and not performing.</summary>
    public bool SplitGridEditable => !_splitAuto && !_isPerforming;

    public int SplitRows
    {
        get => _splitRows;
        set { _splitRows = Math.Clamp(value, 1, 16); OnChanged(); }
    }

    public int SplitColumns
    {
        get => _splitColumns;
        set { _splitColumns = Math.Clamp(value, 1, 16); OnChanged(); }
    }

    /// <summary>Derive a near-square grid from the screen count (4→2×2, 9→3×3, 16→4×4, 6→2×3).</summary>
    private void ApplyAutoGrid()
    {
        var (rows, cols) = AutoGrid(_controller.Monitors.Count);
        SplitRows = rows;
        SplitColumns = cols;
    }

    private static (int rows, int cols) AutoGrid(int screens)
    {
        var n = Math.Max(1, screens);
        var cols = (int)Math.Ceiling(Math.Sqrt(n));
        var rows = (int)Math.Ceiling((double)n / cols);
        return (rows, cols);
    }

    /// <summary>Individual-mode sync toggle: free-run when off (default), shared-clock frame-lock when on.</summary>
    public bool SyncIndividual
    {
        get => _syncIndividual;
        set { _syncIndividual = value; OnChanged(); }
    }

    /// <summary>The sync toggle only applies to Individual mode (the other three modes are inherently synced).</summary>
    public bool SyncToggleVisible => _selectedMode == ShowMode.Individual;

    public AudioOutputDevice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set { _selectedAudioDevice = value; OnChanged(); }
    }

    public bool IsPerforming
    {
        get => _isPerforming;
        private set { _isPerforming = value; OnChanged(); OnChanged(nameof(CanEdit)); OnChanged(nameof(SplitGridEditable)); }
    }

    /// <summary>Assignments are locked while performing (the show is live).</summary>
    public bool CanEdit => !_isPerforming;

    public string Status
    {
        get => _status;
        private set { _status = value; OnChanged(); }
    }

    /// <summary>Apply the current assignments as a show and enter perform mode. Posts and returns.</summary>
    public void Perform()
    {
        if (IsSingleSourceMode)
        {
            if (string.IsNullOrWhiteSpace(SpanSourcePath))
            {
                Status = "Choose a source video before performing.";
                return;
            }
        }
        else if (!Rows.Any(r => !string.IsNullOrWhiteSpace(r.FilePath)))
        {
            Status = "Assign at least one video before performing.";
            return;
        }

        try
        {
            _controller.ApplyShow(BuildShow());
            _controller.EnterPerform();
            Status = $"Performing — {SelectedMode}.";
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"Perform failed: {ex}");
            Status = $"Perform failed: {ex.Message}";
        }
    }

    /// <summary>Leave perform mode (hide outputs, pause the clock). Posts and returns.</summary>
    public void Stop()
    {
        _controller.ExitPerform();
        Status = "Stopped.";
    }

    /// <summary>F11: enter perform mode if idle, leave it if performing.</summary>
    public void TogglePerform()
    {
        if (IsPerforming)
            Stop();
        else
            Perform();
    }

    /// <summary>Space: pause/resume playback without leaving perform mode (frame freezes in place).</summary>
    public void TogglePause()
    {
        if (!IsPerforming)
            return;
        _controller.TogglePause();
        Status = _controller.IsPaused ? "Paused." : $"Performing — {SelectedMode}.";
    }

    /// <summary>Surface a failed global-hotkey registration (e.g. another app owns F11) without crashing.</summary>
    public void NotifyHotkeyUnavailable(string key) =>
        Status = $"Shortcut {key} unavailable (in use by another app); use the buttons.";

    /// <summary>Add an audio file (or a video file's audio) to the mixer.</summary>
    public void AddAudioTrack(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var row = new AudioTrackRow(_controller, Guid.NewGuid().ToString("N"),
            Path.GetFileNameWithoutExtension(filePath), filePath)
        {
            OutputDeviceId = SelectedAudioDevice?.Id,
        };
        AudioTracks.Add(row);
        Status = $"Added audio track '{row.Name}'. (Re-Perform to hear new tracks.)";
    }

    public void RemoveAudioTrack(AudioTrackRow row)
    {
        if (row.IsLinked) // linked tracks follow their video; clear the video to remove them
            return;
        AudioTracks.Remove(row);
        Status = $"Removed audio track '{row.Name}'. (Re-Perform to apply.)";
    }

    // ── Linked (auto-loaded) embedded audio ───────────────────────────────────

    private void OnAssignmentRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MonitorAssignmentRow.FilePath) && !_suppressLinkedRefresh)
            RefreshLinkedAudio();
    }

    /// <summary>
    /// Reconcile the auto-loaded embedded-audio tracks with the videos that actually play in the current
    /// mode: a linked track per source clip that HAS audio, removed when its video is cleared/changed or the
    /// mode no longer uses that slot. Only probes a path when it differs from what's already linked, so this
    /// is cheap to call on every path/mode change. Manual "Add audio…" tracks are untouched.
    /// </summary>
    private void RefreshLinkedAudio()
    {
        // Desired slot → path for the videos shown in this mode.
        var desired = new Dictionary<string, string>();
        if (IsSingleSourceMode)
        {
            if (!string.IsNullOrWhiteSpace(SpanSourcePath))
                desired[SingleSlotKey] = SpanSourcePath;
        }
        else
        {
            foreach (var r in Rows)
                if (!string.IsNullOrWhiteSpace(r.FilePath))
                    desired[r.DeviceId] = r.FilePath;
        }

        // Drop linked tracks whose slot is gone or whose path changed (the add pass re-creates changed ones).
        foreach (var key in _linkedTracks.Keys.ToList())
        {
            if (desired.TryGetValue(key, out var path) && PathEquals(path, _linkedSlotPaths[key]))
                continue;
            AudioTracks.Remove(_linkedTracks[key]);
            _linkedTracks.Remove(key);
            _linkedSlotPaths.Remove(key);
        }

        // Add a linked track for each new/changed slot whose file actually has an audio stream.
        foreach (var (key, path) in desired)
        {
            if (_linkedTracks.ContainsKey(key))
                continue; // unchanged slot already linked above
            if (!_controller.FileHasAudio(path))
                continue; // no audio stream → nothing to add
            var row = new AudioTrackRow(_controller, Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(path), path)
            {
                IsLinked = true,
                OutputDeviceId = SelectedAudioDevice?.Id,
            };
            AudioTracks.Add(row);
            _linkedTracks[key] = row;
            _linkedSlotPaths[key] = path;
        }
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public void Save(string path)
    {
        try
        {
            ProjectService.Save(ToProject(), path);
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"Save failed: {ex}");
            Status = $"Save failed: {ex.Message}";
        }
    }

    public void Load(string path)
    {
        if (IsPerforming)
        {
            Status = "Stop performing before loading a project.";
            return;
        }
        try
        {
            FromProject(ProjectService.Load(path));
            Status = $"Loaded {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            _log.Error("Control", $"Load failed: {ex}");
            Status = $"Load failed: {ex.Message}";
        }
    }

    /// <summary>True if the project has anything worth keeping — used to confirm before <see cref="New"/> discards it.</summary>
    public bool HasContent =>
        Rows.Any(r => !string.IsNullOrWhiteSpace(r.FilePath)) ||
        !string.IsNullOrWhiteSpace(_spanSourcePath) ||
        AudioTracks.Count > 0;

    /// <summary>Reset to a blank project — the app's startup defaults (Individual mode, no clips, no audio,
    /// master 1.0). Reuses the load path so the reset is identical to loading an empty project.</summary>
    public void New()
    {
        if (IsPerforming)
        {
            Status = "Stop performing before starting a new project.";
            return;
        }
        FromProject(new ProjectConfiguration { Mode = ShowMode.Individual });
        Status = "New project.";
    }

    // ── Show / project mapping ────────────────────────────────────────────────

    private ShowDefinition BuildShow()
    {
        var show = new ShowDefinition { Mode = SelectedMode, SyncIndividual = SyncIndividual };
        var isHap = SelectedMode == ShowMode.Hap; // HAP is mode-driven, not a per-file flag

        if (SelectedMode is ShowMode.Individual or ShowMode.Hap)
        {
            foreach (var r in Rows.Where(r => !string.IsNullOrWhiteSpace(r.FilePath)))
                show.Sources.Add(new SourceBinding
                {
                    SourceId = r.DeviceId,
                    FilePath = r.FilePath,
                    MonitorDeviceId = r.DeviceId,
                    IsHap = isHap,
                });
        }
        else
        {
            // Span / split: ONE source (the single "Source video" input) sampled across the outputs.
            show.Sources.Add(new SourceBinding { SourceId = "main", FilePath = SpanSourcePath, IsHap = false });

            if (SelectedMode == ShowMode.Split)
            {
                var (rows, cols) = _splitAuto ? AutoGrid(_controller.Monitors.Count) : (_splitRows, _splitColumns);
                // Empty grid mapping → the controller assigns cells row-major by output index.
                show.WallConfiguration = new VideoWallConfiguration
                {
                    SourceVideoPath = SpanSourcePath, Auto = _splitAuto, Rows = rows, Columns = cols,
                };
            }
        }

        // Audio is the explicit mixer track list (add the video files as tracks if you want their audio).
        foreach (var t in AudioTracks)
            show.AudioTracks.Add(new AudioTrack
            {
                Id = t.Id,
                Name = t.Name,
                SourceFilePath = t.FilePath,
                Volume = t.Volume,
                Pan = t.Pan,
                IsMuted = t.Muted,
                IsSolo = t.Solo,
                OutputDeviceId = t.OutputDeviceId ?? SelectedAudioDevice?.Id,
            });

        return show;
    }

    private ProjectConfiguration ToProject() => new()
    {
        ProjectName = "MultiMon Show",
        Mode = SelectedMode,
        SyncIndividual = SyncIndividual,
        MasterVolume = MasterVolume,
        MasterMuted = MasterMuted,
        SpanSourcePath = string.IsNullOrWhiteSpace(_spanSourcePath) ? null : _spanSourcePath,
        VideoWall = SelectedMode == ShowMode.Split
            ? new VideoWallConfiguration { SourceVideoPath = _spanSourcePath, Auto = _splitAuto, Rows = _splitRows, Columns = _splitColumns }
            : null,
        VideoAssignments = Rows
            .Where(r => !string.IsNullOrWhiteSpace(r.FilePath))
            .Select(r => new VideoAssignment { MonitorDeviceId = r.DeviceId, VideoFilePath = r.FilePath, IsHap = SelectedMode == ShowMode.Hap })
            .ToList(),
        // Persist only manual tracks — linked tracks are re-derived from the videos on load (so they can't
        // double up). Their mixer tweaks reset to defaults on reload (a known limitation).
        AudioTracks = AudioTracks
            .Where(t => !t.IsLinked)
            .Select(t => new AudioTrack
            {
                Id = t.Id,
                Name = t.Name,
                SourceFilePath = t.FilePath,
                Volume = t.Volume,
                Pan = t.Pan,
                IsMuted = t.Muted,
                IsSolo = t.Solo,
                OutputDeviceId = t.OutputDeviceId,
            })
            .ToList(),
    };

    private void FromProject(ProjectConfiguration project)
    {
        // Suppress per-field linked-audio refreshes during the bulk load; reconcile once at the end.
        _suppressLinkedRefresh = true;
        try
        {
            SelectedMode = project.Mode;
            SyncIndividual = project.SyncIndividual;
            MasterVolume = project.MasterVolume;
            MasterMuted = project.MasterMuted;
            SpanSourcePath = project.SpanSourcePath ?? string.Empty;

            // Restore the split grid: an auto grid recomputes for THIS machine's screen count (ignore the
            // saved size); a manual grid restores the explicit rows/columns.
            _splitAuto = project.VideoWall?.Auto ?? true;
            if (_splitAuto)
                ApplyAutoGrid();
            else
            {
                _splitRows = Math.Clamp(project.VideoWall!.Rows, 1, 16);
                _splitColumns = Math.Clamp(project.VideoWall!.Columns, 1, 16);
            }
            OnChanged(nameof(SplitAuto));
            OnChanged(nameof(SplitGridEditable));
            OnChanged(nameof(SplitRows));
            OnChanged(nameof(SplitColumns));
            foreach (var row in Rows)
            {
                var assignment = project.VideoAssignments.FirstOrDefault(a =>
                    string.Equals(a.MonitorDeviceId, row.DeviceId, StringComparison.OrdinalIgnoreCase));
                row.FilePath = assignment?.VideoFilePath ?? string.Empty;
            }

            // Rebuild the mixer from the project's MANUAL tracks; linked tracks are re-derived below.
            AudioTracks.Clear();
            _linkedTracks.Clear();
            _linkedSlotPaths.Clear();
            foreach (var t in project.AudioTracks)
                AudioTracks.Add(new AudioTrackRow(_controller, string.IsNullOrEmpty(t.Id) ? Guid.NewGuid().ToString("N") : t.Id, t.Name, t.SourceFilePath)
                {
                    Volume = t.Volume,
                    Pan = t.Pan,
                    Muted = t.IsMuted,
                    Solo = t.IsSolo,
                    OutputDeviceId = t.OutputDeviceId,
                });
        }
        finally
        {
            _suppressLinkedRefresh = false;
        }

        // Back-compat: an older project (or one saved in a per-monitor mode) may have no SpanSourcePath; fall
        // back to the first assigned clip so a single-source mode still has its input.
        if (IsSingleSourceMode && string.IsNullOrWhiteSpace(_spanSourcePath))
        {
            var first = Rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.FilePath))?.FilePath;
            if (!string.IsNullOrWhiteSpace(first))
            {
                _spanSourcePath = first;
                OnChanged(nameof(SpanSourcePath));
            }
        }
        RefreshLinkedAudio();
    }

    private void OnControllerStateChanged(PerformState state)
    {
        // The controller may raise this off the UI thread (its teardown runs off-thread, the V0087 rule),
        // and setting IsPerforming fires WPF PropertyChanged — which must touch bindings on the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.InvokeAsync(() => IsPerforming = state == PerformState.Performing);
        else
            IsPerforming = state == PerformState.Performing;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
