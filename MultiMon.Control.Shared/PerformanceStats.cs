using System.Diagnostics;
using System.Globalization;
using MultiMon.Core.Abstractions;

namespace MultiMon.Control.Shared;

/// <summary>
/// One output's live counters at the instant <see cref="IPerformanceStatsSource.GetStats"/> sampled them. Every
/// field is a value the render thread publishes with an Interlocked/volatile write and the reader copies — no
/// rate is computed on the render thread (see <see cref="PerformanceReadout"/>, which turns two snapshots into
/// per-second figures on the READER's thread).
/// </summary>
/// <param name="Index">Output index (0-based) — the panel shows <c>Index + 1</c>.</param>
/// <param name="Name">The output window's name, as the log prints it.</param>
/// <param name="SourceId">The id of the source this output samples; empty for the test pattern.</param>
/// <param name="RefreshHz">The display's refresh rate, cached when the controller matched/restored the mode
/// (never queried per tick).</param>
/// <param name="RefreshMatch">What the last EnterPerform did to that rate ("60 Hz -> 50 Hz", "already 50 Hz", …).</param>
/// <param name="PresentCount">Frames COMPLETED on the GPU since the output window was created.</param>
/// <param name="DrawableNulls">Beats skipped this perform because <c>nextDrawable</c> returned null (the
/// compositor was not consuming, including its ~1 s timeout).</param>
/// <param name="InFlightTimeouts">Beats skipped this perform because no uniform ring slot freed in time.</param>
/// <param name="LateFrames">Presents this perform whose selected frame was more than one frame period behind
/// the clock (decode not keeping up).</param>
/// <param name="StartupMs">Milliseconds from EnterPerform to this output's first frame with content, or 0 while
/// that has not happened yet.</param>
/// <param name="ModeSwitchMs">Milliseconds this output's display-refresh match took inside that EnterPerform.</param>
/// <param name="ShowMs">Milliseconds this output's Show (the main-thread window op) took.</param>
public readonly record struct OutputStats(
    int Index, string Name, string SourceId, bool Visible,
    int PixelWidth, int PixelHeight, double RefreshHz, string RefreshMatch,
    long PresentCount, long DrawableNulls, long InFlightTimeouts, long LateFrames,
    double StartupMs, double ModeSwitchMs, double ShowMs, string Phase);

/// <summary>One decode source's live counters. <paramref name="DecodePath"/> is what actually decodes
/// ("hardware"/"software" for VideoToolbox, "HAP CPU" for HAP), read back from the decoder, never assumed.</summary>
public readonly record struct SourceStats(
    string Id, string DecodePath, double FrameRate, double PtsSeconds, long DecodedFrames, bool IsFaulted);

/// <summary>
/// An immutable sample of the whole performance pipeline: the controller's state, every bound output and every
/// live source, plus the <see cref="Stopwatch"/> timestamp the sample was taken at (the basis of every rate).
/// </summary>
public sealed record PerformanceSnapshot(
    PerformState State, long TimestampTicks, IReadOnlyList<OutputStats> Outputs, IReadOnlyList<SourceStats> Sources)
{
    public static PerformanceSnapshot Empty { get; } =
        new(PerformState.Idle, 0, Array.Empty<OutputStats>(), Array.Empty<SourceStats>());
}

/// <summary>
/// A controller that can be polled for live performance counters. Implemented by the Mac
/// <c>PerformanceController</c>; the Windows one does not yet, which is why the view-model treats it as optional.
/// </summary>
public interface IPerformanceStatsSource
{
    /// <summary>
    /// Samples the pipeline's counters on the CALLING thread. Never posts to the controller's worker queue,
    /// never blocks on native work, and never allocates on the render/decode threads — it only reads fields
    /// those threads publish. Safe to call at any state, including Idle.
    /// </summary>
    PerformanceSnapshot GetStats();
}

/// <summary>One output's figures for display, with the rates computed from the previous snapshot.</summary>
public readonly record struct OutputReadout(
    int Index, string Name, int PixelWidth, int PixelHeight, double RefreshHz,
    double PresentsPerSecond, double DecodeFps, string DecodePath, double PtsSeconds, double SourceFrameRate,
    bool SourceFaulted, long LateFrames, long DrawableNulls, double StartupMs, double ModeSwitchMs, double ShowMs,
    string Line);

/// <summary>
/// Turns successive <see cref="PerformanceSnapshot"/>s into per-second rates and the one-line strings the panel
/// binds. ONE instance per reader (the panel's poll, the harness's soak sample): it holds the previous snapshot,
/// so it is not thread-safe and is never shared — which is exactly why no rate is computed on the render thread.
/// The first <see cref="Read"/> after a reset establishes the baseline and reports zero rates.
/// </summary>
public sealed class PerformanceReadout
{
    private PerformanceSnapshot _previous = PerformanceSnapshot.Empty;

    /// <summary>Drops the baseline, so the next <see cref="Read"/> starts a fresh rate window (a new perform).</summary>
    public void Reset() => _previous = PerformanceSnapshot.Empty;

    /// <summary>Rates over the interval since the previous call (zero on the first call / after <see cref="Reset"/>).</summary>
    public IReadOnlyList<OutputReadout> Read(PerformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var seconds = _previous.TimestampTicks > 0 && snapshot.TimestampTicks > _previous.TimestampTicks
            ? (snapshot.TimestampTicks - _previous.TimestampTicks) / (double)Stopwatch.Frequency
            : 0;

        var rows = new List<OutputReadout>(snapshot.Outputs.Count);
        foreach (var output in snapshot.Outputs)
        {
            var source = FindSource(snapshot.Sources, output.SourceId);
            var presentsPerSecond = seconds > 0 ? Delta(output.PresentCount, PreviousPresents(output.Index)) / seconds : 0;
            var decodeFps = seconds > 0 && source is { } s ? Delta(s.DecodedFrames, PreviousDecoded(s.Id)) / seconds : 0;
            rows.Add(new OutputReadout(
                output.Index, output.Name, output.PixelWidth, output.PixelHeight, output.RefreshHz,
                presentsPerSecond, decodeFps, source?.DecodePath ?? string.Empty, source?.PtsSeconds ?? 0,
                source?.FrameRate ?? 0, source?.IsFaulted ?? false, output.LateFrames, output.DrawableNulls,
                output.StartupMs, output.ModeSwitchMs, output.ShowMs,
                Format(output, source, presentsPerSecond, decodeFps)));
        }

        _previous = snapshot;
        return rows;
    }

    /// <summary>
    /// "Output 1 · 4112x2658 @ 50 Hz · 50.0 present/s · decode 25.0 fps (hardware) · pts 3.42 s · late 0 · start 1.8 s".
    /// Fixed decimals throughout; an em dash stands in for a figure that does not exist yet (no source bound, or no
    /// first frame presented).
    /// </summary>
    private static string Format(in OutputStats output, SourceStats? source, double presentsPerSecond, double decodeFps)
    {
        var culture = CultureInfo.InvariantCulture;
        var decode = source is { } s
            ? string.Format(culture, "decode {0:0.0} fps ({1}){2}", decodeFps, s.DecodePath, s.IsFaulted ? " FAULTED" : string.Empty)
            : "decode —";
        var pts = source is { } p ? string.Format(culture, "pts {0:0.00} s", p.PtsSeconds) : "pts —";
        var startup = output.StartupMs > 0 ? string.Format(culture, "start {0:0.0} s", output.StartupMs / 1000) : "start —";
        return string.Format(culture,
            "Output {0} · {1}x{2} @ {3:0.##} Hz · {4:0.0} present/s · {5} · {6} · late {7} · {8}",
            output.Index + 1, output.PixelWidth, output.PixelHeight, output.RefreshHz, presentsPerSecond,
            decode, pts, output.LateFrames, startup);
    }

    private static SourceStats? FindSource(IReadOnlyList<SourceStats> sources, string id)
    {
        if (string.IsNullOrEmpty(id))
            return null;
        foreach (var source in sources)
            if (source.Id == id)
                return source;
        return null;
    }

    private long PreviousPresents(int index)
    {
        foreach (var output in _previous.Outputs)
            if (output.Index == index)
                return output.PresentCount;
        return 0;
    }

    private long PreviousDecoded(string id)
    {
        foreach (var source in _previous.Sources)
            if (source.Id == id)
                return source.DecodedFrames;
        return 0;
    }

    /// <summary>A counter that did not move backwards (a rebuilt source restarts at 0 — report 0, never a negative rate).</summary>
    private static double Delta(long current, long previous) => current > previous ? current - previous : 0;
}
