namespace MultiMon.Core.Abstractions;

/// <summary>
/// A decode source (Media Foundation or HAP). Each source runs its own decode loop OFF the UI
/// and render threads and publishes the latest decoded GPU frame into a triple buffer.
///
/// The GPU texture handoff type is D3D11-specific, so it is defined in MultiMon.Graphics and
/// wired in Milestone 2 — Core only owns the framework-agnostic lifecycle + timing contract,
/// keeping this assembly free of any graphics dependency.
/// </summary>
public interface ISource : IDisposable
{
    /// <summary>Stable id used to bind this source to outputs in a <c>ShowDefinition</c>.</summary>
    string Id { get; }

    bool IsRunning { get; }

    /// <summary>True once the decode loop has given up (no decode path could continue). The output
    /// holds its last frame; the fault is reported, never thrown into the render thread.</summary>
    bool IsFaulted { get; }

    /// <summary>Presentation timestamp of the most recently decoded frame, in media time.
    /// The render thread selects frames against the MasterClock using this — never by seeking.</summary>
    TimeSpan CurrentPts { get; }

    /// <summary>Start the decode loop on its own thread.</summary>
    void Start();

    /// <summary>Signal the decode loop to stop and join it. Off the UI thread (the V0087 rule).</summary>
    void Stop();
}
