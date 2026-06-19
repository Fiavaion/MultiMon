namespace MultiMon.Core.Models;

/// <summary>
/// A normalized texture sampling rectangle in 0..1 source space. Each output samples its slice of a
/// source texture through one of these (REBUILD_ARCHITECTURE.md §2.4). The render pass turns it into
/// 4 floats in a constant buffer; it derives nothing — derivation lives in <see cref="MultiMon.Core.Sync.UvLayout"/>.
/// </summary>
public readonly record struct UvRect(float U0, float V0, float U1, float V1)
{
    /// <summary>The whole source (per-monitor mode; default content binding).</summary>
    public static UvRect Full => new(0f, 0f, 1f, 1f);

    public float Width => U1 - U0;
    public float Height => V1 - V0;
}
