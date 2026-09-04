using System.Text.Json;
using MultiMon.Core;
using MultiMon.Core.Models;

namespace MultiMon.Core.Tests;

/// <summary>
/// Contract tests for ProjectService (ADR 0003 D5): v2 round-trip fidelity, v1→v2 forward migration,
/// and refusal of a newer-than-supported schema. Pure JSON I/O — no GPU.
/// </summary>
public class ProjectServiceTests
{
    private static ProjectConfiguration SampleProject() => new()
    {
        ProjectName = "Test Show",
        Mode = ShowMode.Split,
        VideoAssignments =
        {
            new VideoAssignment { MonitorDeviceId = @"\\.\DISPLAY1", VideoFilePath = @"D:\a.mp4", TimeOffset = TimeSpan.FromMilliseconds(250), Volume = 0.8 },
            new VideoAssignment { MonitorDeviceId = @"\\.\DISPLAY2", VideoFilePath = @"D:\b.mov", IsHap = true },
        },
        VideoWall = new VideoWallConfiguration { SourceVideoPath = @"D:\4k.mp4", SourceWidth = 3840, SourceHeight = 2160, Rows = 2, Columns = 2 },
        AudioTracks =
        {
            new AudioTrack { Name = "Track 1", SourceFilePath = @"D:\a.mp4", Volume = 0.9, OutputDeviceId = "endpoint-xyz" },
        },
    };

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = SampleProject();
        var loaded = ProjectService.Deserialize(ProjectService.Serialize(original));

        Assert.Equal(ProjectConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Test Show", loaded.ProjectName);
        Assert.Equal(ShowMode.Split, loaded.Mode);

        Assert.Equal(2, loaded.VideoAssignments.Count);
        Assert.Equal(@"\\.\DISPLAY1", loaded.VideoAssignments[0].MonitorDeviceId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), loaded.VideoAssignments[0].TimeOffset);
        Assert.Equal(0.8, loaded.VideoAssignments[0].Volume, 6);
        Assert.True(loaded.VideoAssignments[1].IsHap);

        Assert.NotNull(loaded.VideoWall);
        Assert.Equal(3840, loaded.VideoWall!.SourceWidth);
        Assert.Equal(2, loaded.VideoWall.Rows);

        Assert.Single(loaded.AudioTracks);
        Assert.Equal("endpoint-xyz", loaded.AudioTracks[0].OutputDeviceId);
        Assert.Equal(0.9, loaded.AudioTracks[0].Volume, 6);
    }

    [Fact]
    public void Load_LegacyQuadSplitMode_MapsToSplit()
    {
        // A project saved before the QuadSplit→Split rename must still load (the mode is stored by name).
        const string json = """
        { "SchemaVersion": 2, "ProjectName": "Old", "Mode": "QuadSplit",
          "VideoWall": { "SourceVideoPath": "D:\\4k.mp4", "Rows": 2, "Columns": 2 } }
        """;
        var loaded = ProjectService.Deserialize(json);
        Assert.Equal(ShowMode.Split, loaded.Mode);
    }

    [Fact]
    public void Save_WritesCurrentSchemaVersion_EvenIfInstanceStale()
    {
        var project = SampleProject();
        project.SchemaVersion = 1;
        var path = Path.Combine(Path.GetTempPath(), $"mmtest_{Guid.NewGuid():N}.mmproj");
        try
        {
            ProjectService.Save(project, path);
            Assert.Equal(ProjectConfiguration.CurrentSchemaVersion, project.SchemaVersion);
            var loaded = ProjectService.Load(path);
            Assert.Equal(ProjectConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("Test Show", loaded.ProjectName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_V1Project_MigratesToV2_AndIgnoresLegacyCropFields()
    {
        // A pre-rebuild v1 file: unversioned-style + LibVLC crop fields that no longer exist on the model.
        const string v1Json = """
        {
          "SchemaVersion": 1,
          "ProjectName": "Legacy Show",
          "Mode": "Span",
          "VideoAssignments": [
            { "MonitorDeviceId": "\\\\.\\DISPLAY1", "VideoFilePath": "C:\\old.mp4",
              "CropX": 0, "CropY": 0, "CropWidth": 1920, "CropHeight": 1080, "FrameRate": 30.0 }
          ],
          "AudioTracks": [],
          "IsLooping": true,
          "MasterVolume": 100.0,
          "PlaybackSpeed": 1.5
        }
        """;

        var loaded = ProjectService.Deserialize(v1Json);

        Assert.Equal(ProjectConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("Legacy Show", loaded.ProjectName);
        Assert.Equal(ShowMode.Span, loaded.Mode);
        Assert.Single(loaded.VideoAssignments);
        Assert.Equal(@"C:\old.mp4", loaded.VideoAssignments[0].VideoFilePath);
        // Legacy crop fields are simply absent from the lean model — they don't break the load.
    }

    [Fact]
    public void Load_OmittedVersion_DefaultsToCurrent()
    {
        // STJ leaves the model's initializer value when the field is absent → current version.
        const string json = """{ "ProjectName": "No Version" }""";
        var loaded = ProjectService.Deserialize(json);
        Assert.Equal(ProjectConfiguration.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("No Version", loaded.ProjectName);
    }

    [Fact]
    public void Load_NewerSchema_IsRefused()
    {
        string json = $$"""{ "SchemaVersion": {{ProjectConfiguration.CurrentSchemaVersion + 1}}, "ProjectName": "Future" }""";
        Assert.Throws<InvalidDataException>(() => ProjectService.Deserialize(json));
    }

    [Fact]
    public void Deserialize_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProjectService.Deserialize(""));
        Assert.Throws<ArgumentException>(() => ProjectService.Deserialize("   "));
    }

    // ── Load warnings: repaired, never silent ────────────────────────────────

    [Fact]
    public void Load_KnownModes_ProduceNoWarnings()
    {
        foreach (var name in new[] { "Span", "individual", "Hap", "Split", "QuadSplit", "Quad" })
        {
            var loaded = ProjectService.Deserialize($$"""{ "Mode": "{{name}}" }""");
            Assert.Empty(loaded.LoadWarnings);
        }
        Assert.Empty(ProjectService.Deserialize(ProjectService.Serialize(SampleProject())).LoadWarnings);
    }

    [Fact]
    public void Load_UnknownMode_FallsBackToSpan_AndWarns()
    {
        var loaded = ProjectService.Deserialize("""{ "ProjectName": "X", "Mode": "Mosaic" }""");

        Assert.Equal(ShowMode.Span, loaded.Mode);
        var warning = Assert.Single(loaded.LoadWarnings);
        Assert.Contains("Mosaic", warning);
        Assert.Contains("Span", warning);
    }

    [Fact]
    public void Load_NonStringMode_FallsBackToSpan_AndWarns()
    {
        var loaded = ProjectService.Deserialize("""{ "Mode": 7 }""");
        Assert.Equal(ShowMode.Span, loaded.Mode);
        Assert.Contains(loaded.LoadWarnings, w => w.Contains("7"));
    }

    [Fact]
    public void Load_UnknownMode_IsLogged()
    {
        var log = new CapturingLog();
        ProjectService.Deserialize("""{ "Mode": "Mosaic" }""", log);
        Assert.Contains(log.Lines, l => l.Contains("Mosaic"));
    }

    [Fact]
    public void Load_NullLists_TreatedAsEmpty_AndWarned()
    {
        var loaded = ProjectService.Deserialize("""{ "VideoAssignments": null, "AudioTracks": null }""");

        Assert.NotNull(loaded.VideoAssignments);
        Assert.Empty(loaded.VideoAssignments);
        Assert.NotNull(loaded.AudioTracks);
        Assert.Empty(loaded.AudioTracks);
        Assert.Equal(2, loaded.LoadWarnings.Count);
    }

    [Fact]
    public void Load_NullListEntries_Dropped_AndWarned()
    {
        var loaded = ProjectService.Deserialize("""
        { "VideoAssignments": [ null, { "MonitorDeviceId": "m1", "VideoFilePath": "a.mp4" }, null ],
          "AudioTracks": [ null ] }
        """);

        var a = Assert.Single(loaded.VideoAssignments);
        Assert.Equal("m1", a.MonitorDeviceId);
        Assert.Empty(loaded.AudioTracks);
        Assert.Equal(2, loaded.LoadWarnings.Count);
    }

    [Fact]
    public void LoadWarnings_AreNeverSerialized()
    {
        var project = SampleProject();
        project.LoadWarnings.Add("stale");
        Assert.DoesNotContain("LoadWarnings", ProjectService.Serialize(project));
    }

    // ── Hostile input stays bounded ──────────────────────────────────────────

    [Fact]
    public void Deserialize_DeeplyNested_ThrowsJsonException_NotStackOverflow()
    {
        const int depth = 10_000;
        var json = "{ \"VideoWall\": " + new string('[', depth) + new string(']', depth) + " }";
        Assert.ThrowsAny<JsonException>(() => ProjectService.Deserialize(json)); // JsonReaderException derives from it
    }

    [Fact]
    public void Deserialize_NonObjectRoot_Refused()
    {
        Assert.Throws<InvalidDataException>(() => ProjectService.Deserialize("[1, 2, 3]"));
        Assert.Throws<InvalidDataException>(() => ProjectService.Deserialize("\"just a string\""));
    }

    [Fact]
    public void Deserialize_HugeString_WithinCap_Loads_Intact()
    {
        var name = new string('n', 1 << 20); // 1 MiB project name: absurd but under the cap → loads verbatim
        var loaded = ProjectService.Deserialize($$"""{ "ProjectName": "{{name}}" }""");
        Assert.Equal(name.Length, loaded.ProjectName.Length);
    }

    [Fact]
    public void Deserialize_OverCap_RefusedBeforeParsing()
    {
        var json = "{ \"ProjectName\": \"" + new string('n', (int)ProjectService.MaxProjectBytes) + "\" }";
        Assert.Throws<InvalidDataException>(() => ProjectService.Deserialize(json));
    }

    [Fact]
    public void Load_OversizedFile_RefusedBeforeReading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mmtest_{Guid.NewGuid():N}.mmproj");
        try
        {
            using (var f = File.Create(path))
                f.SetLength(ProjectService.MaxProjectBytes + 1); // sparse: no actual bytes written
            Assert.Throws<InvalidDataException>(() => ProjectService.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class CapturingLog : MultiMon.Core.Diagnostics.ILog
    {
        public List<string> Lines { get; } = new();
        public void Info(string source, string message)  => Lines.Add(message);
        public void Debug(string source, string message) => Lines.Add(message);
        public void Error(string source, string message) => Lines.Add(message);
    }
}
