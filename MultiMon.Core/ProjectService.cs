using System.Text.Json;
using System.Text.Json.Serialization;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Core;

/// <summary>
/// Save/load of <c>.mmproj</c> project files (ADR 0003 D5). Pure I/O over
/// <see cref="ProjectConfiguration"/> — no UI, no GPU — so it is unit-testable in
/// <c>MultiMon.Tests</c>. Forward-compatible: a v1 (old LibVLC) file is migrated to v2 on load;
/// an unknown HIGHER version is refused (never crash with a corrupt half-load).
///
/// <para><b>Hostile input stays bounded:</b> a file over <see cref="MaxProjectBytes"/> is refused before it
/// is read, and the parser's default depth limit (64) turns a deeply nested document into a
/// <see cref="JsonException"/> rather than a stack overflow. Non-fatal oddities (an unknown mode name, null
/// lists or entries) are repaired and reported through <see cref="ProjectConfiguration.LoadWarnings"/> —
/// and to <c>log</c> when one is supplied — never silently.</para>
/// </summary>
public static class ProjectService
{
    /// <summary>Largest project file this build will open. A real project is a few KB; anything near this
    /// is not one, and refusing it up front keeps a hostile file from being read into memory at all.</summary>
    public const long MaxProjectBytes = 16 * 1024 * 1024;

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

    /// <summary>Load and migrate a project from <paramref name="path"/>. Load warnings (non-fatal repairs)
    /// are returned on <see cref="ProjectConfiguration.LoadWarnings"/> and written to <paramref name="log"/>.</summary>
    public static ProjectConfiguration Load(string path, ILog? log = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var length = new FileInfo(path).Length;
        if (length > MaxProjectBytes)
            throw new InvalidDataException($"Project file is {length:N0} bytes; the limit is {MaxProjectBytes:N0}. This is not a MultiMon project.");

        return Deserialize(File.ReadAllText(path), log);
    }

    /// <summary>Deserialize + migrate a project from a JSON string (see <see cref="Load"/> for the warning contract).</summary>
    public static ProjectConfiguration Deserialize(string json, ILog? log = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("Project JSON is empty.", nameof(json));
        if (json.Length > MaxProjectBytes)
            throw new InvalidDataException($"Project JSON is {json.Length:N0} characters; the limit is {MaxProjectBytes:N0}.");

        // Parse to a document first (one parse — the object is materialised from the element) so the raw
        // "Mode" value can be inspected: the converter maps an unknown name to Span to keep the load alive,
        // and this is where that fallback is reported instead of silently taking effect.
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Project JSON root must be an object, not {root.ValueKind}.");

        var project = root.Deserialize<ProjectConfiguration>(Options)
            ?? throw new InvalidDataException("Project JSON deserialized to null.");

        if (root.TryGetProperty(nameof(ProjectConfiguration.Mode), out var modeEl) && !ShowModeJsonConverter.IsKnown(modeEl))
            project.LoadWarnings.Add($"Unknown show mode {modeEl.GetRawText()}; using {project.Mode}.");

        Migrate(project);

        foreach (var warning in project.LoadWarnings)
            log?.Error("Project", $"load warning: {warning}");
        return project;
    }

    /// <summary>
    /// Bring a loaded project up to the current schema. v0/v1 (pre-rebuild or unversioned) files are
    /// accepted and tagged v2 — they had no rebuild-specific fields to translate beyond what the lean
    /// model already deserializes (the dropped LibVLC crop fields are simply ignored by the parser).
    /// A version NEWER than this build is refused (we can't know its shape). Explicit <c>null</c> lists
    /// and null list entries are repaired to empty/absent (with a warning) so callers can index freely.
    /// </summary>
    private static void Migrate(ProjectConfiguration project)
    {
        if (project.SchemaVersion > ProjectConfiguration.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Project schema v{project.SchemaVersion} is newer than this build supports " +
                $"(v{ProjectConfiguration.CurrentSchemaVersion}). Update MultiMon to open it.");
        }

        if (project.SchemaVersion < ProjectConfiguration.CurrentSchemaVersion)
            project.SchemaVersion = ProjectConfiguration.CurrentSchemaVersion;

        project.VideoAssignments = RepairList(project.VideoAssignments, nameof(project.VideoAssignments), project.LoadWarnings);
        project.AudioTracks = RepairList(project.AudioTracks, nameof(project.AudioTracks), project.LoadWarnings);
    }

    private static List<T> RepairList<T>(List<T>? list, string name, List<string> warnings) where T : class
    {
        if (list is null)
        {
            warnings.Add($"{name} was null; treated as empty.");
            return new List<T>();
        }
        var dropped = list.RemoveAll(item => item is null);
        if (dropped > 0)
            warnings.Add($"{name} had {dropped} null entr{(dropped == 1 ? "y" : "ies")}; dropped.");
        return list;
    }
}

/// <summary>Reads/writes <see cref="ShowMode"/> by name, accepting the legacy "QuadSplit" alias for the
/// renamed <see cref="ShowMode.Split"/> so projects saved before the rename still load. An unknown value
/// falls back to <see cref="ShowMode.Span"/> (the load survives); <see cref="ProjectService"/> reports it.</summary>
internal sealed class ShowModeJsonConverter : JsonConverter<ShowMode>
{
    /// <summary>True if the raw JSON value names a mode this converter understands (including aliases).</summary>
    public static bool IsKnown(JsonElement element)
        => element.ValueKind == JsonValueKind.String && TryParse(element.GetString(), out _);

    private static bool TryParse(string? s, out ShowMode mode)
    {
        if (string.Equals(s, "QuadSplit", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s, "Quad", StringComparison.OrdinalIgnoreCase))
        {
            mode = ShowMode.Split;
            return true;
        }
        // Enum.TryParse also accepts the underlying integer as text ("2"); that is a fine, if odd, spelling.
        return Enum.TryParse(s, ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    public override ShowMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return TryParse(s, out var mode) ? mode : ShowMode.Span;
    }

    public override void Write(Utf8JsonWriter writer, ShowMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
