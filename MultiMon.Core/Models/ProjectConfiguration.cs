using System.Text.Json.Serialization;

namespace MultiMon.Core.Models;

/// <summary>
/// A saved MultiMon project (<c>.mmproj</c>). Salvaged in SHAPE from the old app
/// (REBUILD_ARCHITECTURE.md §7) and re-versioned for the rebuild: the LibVLC crop strings are gone
/// (UV is derived), playback-speed is dropped (no live-decoder speed control), and a
/// <see cref="SchemaVersion"/> gates forward-compatible loading (ADR 0003 D5).
/// </summary>
public sealed class ProjectConfiguration
{
    /// <summary>Current schema version written by this build. v1 = old LibVLC format; v2 = rebuild.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Schema version of THIS instance. <see cref="ProjectService"/> migrates v1 → v2 on load.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string ProjectName { get; set; } = string.Empty;

    /// <summary>How the sources bind to monitors for this project.</summary>
    public ShowMode Mode { get; set; } = ShowMode.Span;

    /// <summary>Individual-mode sync toggle (see <see cref="ShowDefinition.SyncIndividual"/>).</summary>
    public bool SyncIndividual { get; set; }

    /// <summary>One entry per assigned monitor slot (used by Individual / Hap, the per-monitor modes).</summary>
    public List<VideoAssignment> VideoAssignments { get; set; } = new();

    /// <summary>The single source clip for the one-source modes (Span / Split); null otherwise.
    /// Additive in schema v2 — older projects simply leave it null and fall back to the first assignment.</summary>
    public string? SpanSourcePath { get; set; }

    /// <summary>Grid config for <see cref="ShowMode.Split"/>; null otherwise.</summary>
    public VideoWallConfiguration? VideoWall { get; set; }

    /// <summary>Audio mixer tracks (volume/pan/mute/solo + device routing).</summary>
    public List<AudioTrack> AudioTracks { get; set; } = new();

    /// <summary>Master mixer volume (0–1) and mute.</summary>
    public double MasterVolume { get; set; } = 1.0;
    public bool MasterMuted { get; set; }

    /// <summary>Non-fatal repairs made while loading (unknown mode name, null lists) — the load succeeded
    /// but not verbatim, and the user should be told. Filled by <c>ProjectService</c>; never serialized.</summary>
    [JsonIgnore]
    public List<string> LoadWarnings { get; } = new();

    public override string ToString()
        => $"{ProjectName} [{Mode}] ({VideoAssignments.Count} assignments, schema v{SchemaVersion})";
}
