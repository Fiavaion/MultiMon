namespace MultiMon.Decode.Hap;

/// <summary>
/// Allocation ceilings for parsing UNTRUSTED .mov/HAP input. The threat: a crafted file whose header
/// claims an enormous size or entry count, making the parser allocate gigabytes (OOM) before it reads any
/// real data. Tables backed by file bytes are bounded against the buffer length directly (you can't have
/// more entries than the buffer can hold); these constants bound the two allocations that are NOT
/// buffer-backed. Both sit far above any real HAP asset (8K BC3 ≈ 33 MB/frame, moov a few MB even for long
/// clips) — they cap the blast radius to <see cref="InvalidDataException"/> instead of OutOfMemoryException.
/// </summary>
internal static class HapLimits
{
    /// <summary>Max in-memory size of the moov atom (the sample tables read up front).</summary>
    public const long MaxMoovBytes = 256L * 1024 * 1024;

    /// <summary>Max decoded size of a single Snappy block / HAP chunk.</summary>
    public const int MaxDecodedBytes = 256 * 1024 * 1024;
}
