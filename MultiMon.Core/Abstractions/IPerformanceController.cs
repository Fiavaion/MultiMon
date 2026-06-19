using MultiMon.Core.Models;

namespace MultiMon.Core.Abstractions;

/// <summary>Lifecycle state of a perform session, surfaced to the control UI.</summary>
public enum PerformState
{
    /// <summary>No show applied / not performing.</summary>
    Idle,
    /// <summary>A show is applied and the outputs are presenting fullscreen.</summary>
    Performing
}

/// <summary>
/// The control-side contract for driving a performance (ADR 0003 D4). This is the ONLY surface the WPF
/// control panel sees — it references Core types only, never a D3D11/Decode type, and every method
/// posts-and-returns so the UI thread never blocks on native teardown (the V0087 rule). The concrete
/// implementation lives in <c>MultiMon.Graphics</c> and owns the render/decode/present pipeline.
/// </summary>
public interface IPerformanceController : IDisposable
{
    /// <summary>Current lifecycle state.</summary>
    PerformState State { get; }

    /// <summary>The monitors this controller drives (one output window each), for the assignment UI.</summary>
    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>
    /// Builds the sources + per-output bindings for <paramref name="show"/> on the invariant pipeline
    /// (re-binding only; never rebuilds the device/swapchains). Tears down any previously-applied show
    /// first. Safe to call while idle or performing.
    /// </summary>
    void ApplyShow(ShowDefinition show);

    /// <summary>Shows the output windows and starts the master clock. No-op if already performing or no show applied.</summary>
    void EnterPerform();

    /// <summary>Pauses the clock and hides the output windows (content stays bound). Posts and returns.</summary>
    void ExitPerform();

    /// <summary>True while performing AND the clock is paused (frame frozen, windows still shown).</summary>
    bool IsPaused { get; }

    /// <summary>
    /// Pause/resume playback WITHOUT leaving perform mode: stops/starts the clock(s) so the frame freezes
    /// in place while the output windows stay visible. No-op unless performing. Posts and returns.
    /// </summary>
    void TogglePause();

    /// <summary>Available WASAPI render endpoints for audio routing (the device-selection UI reads this).</summary>
    IReadOnlyList<AudioOutputDevice> GetAudioDevices();

    /// <summary>True if the media file has a decodable audio stream — used to auto-add a loaded video's
    /// audio to the mixer. Best-effort probe (false on any failure); keeps decode types behind the firewall.</summary>
    bool FileHasAudio(string filePath);

    // ── Live audio mixer (M7 Stage B) — applied immediately during perform; no-op if no audio is playing ──
    void SetMasterVolume(double volume);
    void SetMasterMuted(bool muted);
    void SetTrackVolume(string trackId, double volume);
    void SetTrackMuted(string trackId, bool muted);
    void SetTrackSolo(string trackId, bool solo);
    void SetTrackPan(string trackId, double pan);

    /// <summary>Raised when <see cref="State"/> changes (marshal to the UI thread in the handler).</summary>
    event Action<PerformState>? StateChanged;
}
