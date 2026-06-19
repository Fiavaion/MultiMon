using MultiMon.Core;
using MultiMon.Core.Models;

namespace MultiMon.Tests;

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
}
