using System.Diagnostics;

namespace MultiMon.Core.Timing;

/// <summary>
/// The single monotonic playback timeline (ADR 0002 D1), based on <see cref="Stopwatch"/>'s
/// QueryPerformanceCounter. Started when a show enters perform mode; every source selects the frame
/// nearest <see cref="CurrentMediaTime"/> so all outputs stay content-synced regardless of their
/// present rate. Media time accumulates across Stop/Start (pause) and resets to zero on Reset.
///
/// Thread-safe: the render thread reads <see cref="CurrentMediaTime"/> every present while the
/// controller may Start/Stop from another thread. QPC is monotonic, so time never goes backwards.
/// </summary>
public sealed class MasterClock
{
    private readonly object _gate = new();
    private long _accumulatedTicks;       // media ticks banked from completed run segments
    private long _segmentStartTimestamp;  // QPC timestamp when the current run segment began
    private bool _running;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Media time since the first Start, excluding paused spans. Zero until first Start.</summary>
    public TimeSpan CurrentMediaTime
    {
        get { lock (_gate) return TimeSpan.FromSeconds(TotalTicks() / (double)Stopwatch.Frequency); }
    }

    /// <summary>Begin or resume the timeline from the currently held media time. No-op if running.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _segmentStartTimestamp = Stopwatch.GetTimestamp();
            _running = true;
        }
    }

    /// <summary>Pause the timeline; <see cref="CurrentMediaTime"/> holds its value. No-op if stopped.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _accumulatedTicks += Stopwatch.GetTimestamp() - _segmentStartTimestamp;
            _running = false;
        }
    }

    /// <summary>Stop and return <see cref="CurrentMediaTime"/> to zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _running = false;
            _accumulatedTicks = 0;
        }
    }

    private long TotalTicks() =>
        _running ? _accumulatedTicks + (Stopwatch.GetTimestamp() - _segmentStartTimestamp) : _accumulatedTicks;
}
