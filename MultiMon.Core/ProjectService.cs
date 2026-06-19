using System.Text.Json;
using System.Text.Json.Serialization;
using MultiMon.Core.Models;

namespace MultiMon.Core;

/// <summary>
/// Save/load of <c>.mmproj</c> project files (ADR 0003 D5). Pure I/O over
/// <see cref="ProjectConfiguration"/> — no UI, no GPU — so it is unit-testable in
/// <c>MultiMon.Tests</c>. Forward-compatible: a v1 (old LibVLC) file is migrated to v2 on load;
/// an unknown HIGHER version is refused (never crash with a corrupt half-load).
/// </summary>
public static class ProjectService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // ShowMode converter first so it wins over the generic enum converter (which matches any enum).
        Converters = { new ShowModeJsonConverter(), new JsonStringEnumConverter() },
    };

    /// <summary>Serialize a project to <paramref name="path"/> (always written at the current schema version).</summary>
    public static void Save(ProjectConfiguration project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        project.SchemaVersion = ProjectConfiguration.CurrentSchemaVersion;
        File.WriteAllText(path, Serialize(project));
    }

    /// <summary>Serialize a project to a JSON string (the unit of the round-trip test).</summary>
    public static string Serialize(ProjectConfiguration project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, Options);
    }

    /// <summary>Load and migrate a project from <paramref name="path"/>.</summary>
    public static ProjectConfiguration Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));
        return Deserialize(File.ReadAllText(path));
    }

    /// <summary>Deserialize + migrate a project from a JSON string.</summary>
    public static ProjectConfiguration Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Project JSON is empty.", nameof(json));

        var project = JsonSerializer.Deserialize<ProjectConfiguration>(json, Options)
            ?? throw new InvalidDataException("Project JSON deserialized to null.");

        return Migrate(project);
    }

    /// <summary>
    /// Bring a loaded project up to the current schema. v0/v1 (pre-rebuild or unversioned) files are
    /// accepted and tagged v2 — they had no rebuild-specific fields to translate beyond what the lean
    /// model already deserializes (the dropped LibVLC crop fields are simply ignored by the parser).
    /// A version NEWER than this build is refused (we can't know its shape).
    /// </summary>
    private static ProjectConfiguration Migrate(ProjectConfiguration project)
    {
        if (project.SchemaVersion > ProjectConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Project schema v{project.SchemaVersion} is newer than this build supports " +
                $"(v{ProjectConfiguration.CurrentSchemaVersion}). Update MultiMon to open it.");
        }

        if (project.SchemaVersion < ProjectConfiguration.CurrentSchemaVersion)
            project.SchemaVersion = ProjectConfiguration.CurrentSchemaVersion;

        return project;
    }
}

/// <summary>Reads/writes <see cref="ShowMode"/> by name, accepting the legacy "QuadSplit" alias for the
/// renamed <see cref="ShowMode.Split"/> so projects saved before the rename still load.</summary>
internal sealed class ShowModeJsonConverter : JsonConverter<ShowMode>
{
    public override ShowMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (string.Equals(s, "QuadSplit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "Quad", StringComparison.OrdinalIgnoreCase))
            return ShowMode.Split;
        return Enum.TryParse<ShowMode>(s, ignoreCase: true, out var mode) ? mode : ShowMode.Span;
    }

    public override void Write(Utf8JsonWriter writer, ShowMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
