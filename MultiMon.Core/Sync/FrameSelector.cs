namespace MultiMon.Core.Sync;

/// <summary>
/// Pure frame-selection: given the presentation timestamps of buffered frames (ascending) and a
/// target media time, pick which frame to show (ADR 0002 D2). This is the unit of drift correction —
/// the render thread drops or repeats frames by index instead of seeking the decoder
/// (decode-threading.md). Pure and allocation-free so it is trivially unit-tested and has no threading
/// concerns of its own.
/// </summary>
public static class FrameSelector
{
    /// <summary>
    /// Returns the index of the frame whose PTS is the greatest value &lt;= <paramref name="targetTime"/>.
    /// If <paramref name="targetTime"/> precedes every frame, returns 0 (show the oldest buffered frame
    /// rather than nothing). <paramref name="framePts"/> must be non-empty and ascending.
    /// </summary>
    public static int Select(IReadOnlyList<TimeSpan> framePts, TimeSpan targetTime)
    {
        if (framePts is null || framePts.Count == 0)
            throw new ArgumentException("framePts must be non-empty.", nameof(framePts));

        // Binary search for the last index with pts <= target. result stays 0 if target precedes every
        // frame (show the oldest), which is the documented contract.
        int lo = 0, hi = framePts.Count - 1, result = 0;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (framePts[mid] <= targetTime)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }
}
