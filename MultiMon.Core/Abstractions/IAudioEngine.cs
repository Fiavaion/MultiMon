namespace MultiMon.Core.Abstractions;

/// <summary>
/// Routes audio tracks to output devices via raw shared-mode WASAPI, clocked to the MasterClock.
/// Implemented in MultiMon.Audio in Milestone 6 — declared here so the orchestrator can depend on
/// the contract, not the implementation.
/// </summary>
public interface IAudioEngine : IDisposable
{
    void Start();
    void Stop();
}
