using MultiMon.Core.Models;

namespace MultiMon.Core.Tests;

/// <summary>
/// Milestone 0 smoke: the salvaged framework-agnostic Core models instantiate and compute correctly.
/// The real success-metric gate is the stress harness (MultiMon.Stress) — these guard the pure logic
/// the harness and pipeline build on.
/// </summary>
public class CoreModelsSmokeTests
{
    [Fact]
    public void MonitorRect_ComputesEdges()
    {
        var r = new MonitorRect(100, 200, 1920, 1080);
        Assert.Equal(100, r.Left);
        Assert.Equal(200, r.Top);
        Assert.Equal(2020, r.Right);
        Assert.Equal(1280, r.Bottom);
    }

    [Fact]
    public void MonitorInfo_ToString_IncludesPrimaryFlag()
    {
        var m = new MonitorInfo
        {
            DisplayName = "Monitor 1 (Primary)",
            Resolution = "1920x1080",
            RefreshRate = 60,
            IsPrimary = true
        };
        Assert.Contains("[Primary]", m.ToString());
        Assert.Contains("1920x1080", m.ToString());
    }

    [Fact]
    public void ShowDefinition_DefaultsToSpanning()
    {
        var show = new ShowDefinition();
        Assert.Equal(ShowMode.Span, show.Mode);
        Assert.Empty(show.Sources);
    }

    [Fact]
    public void VideoWallConfiguration_TotalCells_IsColumnsTimesRows()
    {
        var wall = new VideoWallConfiguration { Columns = 2, Rows = 2 };
        Assert.Equal(4, wall.TotalCells);
    }
}
