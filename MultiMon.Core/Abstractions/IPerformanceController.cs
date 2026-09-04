using MultiMon.Core.Models;

namespace MultiMon.Core.Abstractions;

/// <summary>Lifecycle state of a perform session, surfaced to the control UI.</summary>
public enum PerformState
{
    /// <summary>No show applied / not performing.</summary>
    Idle,
    /// <summary>A show is applied and the outputs are presenting fullscreen.</summary>
    Performing,
    /// <summary>Performing, but the clock is stopped: the frame is frozen in place, windows still shown.</summary>
    Paused
}

/// <summary>
/// The control-side contract for driving a performance (ADR 0003 D4). This is the ONLY surface the WPF
/// control panel sees — it references Core types only, never a D3D11/Decode type. Every command
/// posts to the controller's own worker thread and returns, so the UI thread never runs or blocks on a
/// native build/teardown (the V0087 rule); results arrive through <see cref="StateChanged"/> and
/// <see cref="CommandFailed"/>. Commands run strictly in the order they were issued. The concrete
/// implementation lives in <c>MultiMon.Control</c> (composition root) and owns the render/decode/present
/// pipeline.
/// </summary>
public interface IPerformanceController : IDisposable
{
    /// <summary>Current lifecycle state (updated on the worker; read is safe from any thread).</summary>
    PerformState State { get; }

    /// <summary>The monitors this controller drives (one output window each), for the assignment UI.</summary>
    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>
    /// Builds the sources + per-output bindings for <paramref name="show"/> on the invariant pipeline
    /// (re-binding only; never rebuilds the device/swapchains). Tears down any previously-applied show
    /// first. Safe to call while idle or performing. Posts and returns; a build failure is reported via
    /// <see cref="CommandFailed"/>.
    /// </summary>
    void ApplyShow(ShowDefinition show);

    /// <summary>Shows the output windows and starts the master clock. No-op if already performing; reports
    /// <see cref="CommandFailed"/> if the applied show opened no source. Posts and returns.</summary>
    void EnterPerform();

    /// <summary>Pauses the clock and hides the output windows (content stays bound). Posts and returns.</summary>
    void ExitPerform();

    /// <summary>
    /// Pause/resume playback WITHOUT leaving perform mode: stops/starts the clock(s) so the frame freezes
    /// in place while the output windows stay visible (<see cref="PerformState.Paused"/>). No-op unless
    /// performing. Posts and returns.
    /// </summary>
    void TogglePause();

    /// <summary>Available WASAPI render endpoints for audio routing (the device-selection UI reads this).</summary>
    IReadOnlyList<AudioOutputDevice> GetAudioDevices();

    /// <summary>True if the media file has an audible audio stream — used to auto-add a loaded video's
    /// audio to the mixer. A full Media Foundation decode probe: NEVER call on the UI thread (run it on a
    /// Task and marshal the result back). Best-effort (false on any failure); keeps decode types behind
    /// the firewall.</summary>
    bool FileHasAudio(string filePath);

    // ── Live audio mixer (M7 Stage B) — applied during perform; no-op if no audio is playing. Posted. ──
    void SetMasterVolume(double volume);
    void SetMasterMuted(bool muted);
    void SetTrackVolume(string trackId, double volume);
    void SetTrackMuted(string trackId, bool muted);
    void SetTrackSolo(string trackId, bool solo);
    void SetTrackPan(string trackId, double pan);

    /// <summary>Raised on the controller's worker thread when <see cref="State"/> changes — marshal to the
    /// UI thread in the handler.</summary>
    event Action<PerformState>? StateChanged;

    /// <summary>Raised on the controller's worker thread when a posted command failed (message for the
    /// status line; the full exception is in the log). Marshal to the UI thread in the handler.</summary>
    event Action<string>? CommandFailed;
}
