using MultiMon.Core.Models;

namespace MultiMon.Core.Abstractions;

/// <summary>Enumerates display monitors (Win32 implementation lives in MultiMon.Platform).</summary>
public interface IMonitorService
{
    event EventHandler? MonitorsChanged;
    IReadOnlyList<MonitorInfo> GetMonitors();
    void RefreshMonitors();
}
