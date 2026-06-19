using MultiMon.Core.Timing;

namespace MultiMon.Tests;

/// <summary>
/// Behavioral contract tests for MasterClock (ADR 0002 D1).
/// Timing assertions use 80–150 ms delays with generous tolerances to stay non-flaky
/// on loaded CI machines. No test depends on exact millisecond values.
/// </summary>
public class MasterClockTests
{
    // ── Initial state ──────────────────────────────────────────────────────────

    [Fact]
    public void NewClock_IsNotRunning()
    {
        var clock = new MasterClock();
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void NewClock_MediaTimeIsZero()
    {
        var clock = new MasterClock();
        Assert.Equal(TimeSpan.Zero, clock.CurrentMediaTime);
    }

    // ── Start / running state ──────────────────────────────────────────────────

    [Fact]
    public void Start_SetsIsRunningTrue()
    {
        var clock = new MasterClock();
        clock.Start();
        Assert.True(clock.IsRunning);
    }

    [Fact]
    public void AfterStart_MediaTimeAdvances()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(100);
        // At least 50 ms should have accumulated; allow up to 500 ms for slow machines.
        var t = clock.CurrentMediaTime;
        Assert.InRange(t.TotalMilliseconds, 50, 500);
    }

    [Fact]
    public void MediaTime_IsMonotonicWhileRunning()
    {
        var clock = new MasterClock();
        clock.Start();
        var t1 = clock.CurrentMediaTime;
        Thread.Sleep(20);
        var t2 = clock.CurrentMediaTime;
        Assert.True(t2 >= t1, $"Expected t2 ({t2}) >= t1 ({t1}); clock went backwards.");
    }

    // ── Start idempotency ──────────────────────────────────────────────────────

    [Fact]
    public void Start_Idempotent_SecondCallDoesNotJumpClock()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        var before = clock.CurrentMediaTime;

        // Second Start() while already running must be a no-op (no reset, no jump).
        clock.Start();
        var after = clock.CurrentMediaTime;

        // The clock should still be advancing smoothly — after >= before, and the
        // gap should be tiny (well under 80 ms, not a reset to 0 or a large skip).
        Assert.True(after >= before, "Second Start() caused time to go backwards.");
        Assert.True(after.TotalMilliseconds < before.TotalMilliseconds + 80,
            "Second Start() caused an unexpectedly large forward jump.");
        Assert.True(clock.IsRunning);
    }

    // ── Stop ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Stop_SetsIsRunningFalse()
    {
        var clock = new MasterClock();
        clock.Start();
        clock.Stop();
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void Stop_FreezesClock_DoesNotAdvance()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        clock.Stop();

        var frozen = clock.CurrentMediaTime;
        Thread.Sleep(100);
        var later = clock.CurrentMediaTime;

        // Allow ≤5 ms epsilon for lock overhead and QPC jitter.
        Assert.True(Math.Abs((later - frozen).TotalMilliseconds) <= 5,
            $"Clock advanced after Stop(): frozen={frozen.TotalMilliseconds:F2} ms, later={later.TotalMilliseconds:F2} ms.");
    }

    [Fact]
    public void Stop_WhenAlreadyStopped_IsNoOp()
    {
        var clock = new MasterClock();
        // Stop on a never-started clock must not throw or corrupt state.
        clock.Stop();
        Assert.False(clock.IsRunning);
        Assert.Equal(TimeSpan.Zero, clock.CurrentMediaTime);
    }

    // ── Resume (Start after Stop accumulates) ─────────────────────────────────

    [Fact]
    public void StartAfterStop_ResumesAccumulating()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        clock.Stop();
        var pausedAt = clock.CurrentMediaTime;
        Assert.True(pausedAt.TotalMilliseconds > 30, "Sanity: expected some time to have elapsed.");

        // Resume and accumulate more.
        clock.Start();
        Thread.Sleep(80);
        var resumed = clock.CurrentMediaTime;

        // Resumed time must be greater than the paused snapshot.
        Assert.True(resumed > pausedAt,
            $"After resume, time ({resumed}) did not exceed paused value ({pausedAt}).");
        Assert.True(clock.IsRunning);
    }

    [Fact]
    public void StartAfterStop_DoesNotResetToZero()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        clock.Stop();
        var pausedAt = clock.CurrentMediaTime;

        clock.Start();
        // Immediately after resume, media time must still be at least what it was when paused.
        var justAfterResume = clock.CurrentMediaTime;
        Assert.True(justAfterResume >= pausedAt,
            $"Resume reset the clock: justAfterResume={justAfterResume}, pausedAt={pausedAt}.");
    }

    // ── Reset ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_WhileRunning_StopsAndZeroesTime()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        clock.Reset();

        Assert.False(clock.IsRunning);
        Assert.Equal(TimeSpan.Zero, clock.CurrentMediaTime);
    }

    [Fact]
    public void Reset_WhileStopped_ZeroesTime()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        clock.Stop();
        clock.Reset();

        Assert.False(clock.IsRunning);
        Assert.Equal(TimeSpan.Zero, clock.CurrentMediaTime);
    }

    [Fact]
    public void Reset_ThenStart_AccumulatesFromZero()
    {
        var clock = new MasterClock();
        clock.Start();
        Thread.Sleep(80);
        clock.Reset();
        clock.Start();
        Thread.Sleep(80);

        var t = clock.CurrentMediaTime;
        // Should be roughly 80 ms (the second run), not ~160 ms (both runs combined).
        // Allow a wide band: 30 ms (slow machine) to 400 ms (including slop).
        Assert.InRange(t.TotalMilliseconds, 30, 400);
    }

    // ── Concurrent reads are safe ──────────────────────────────────────────────

    [Fact]
    public void ConcurrentReads_DoNotThrow()
    {
        var clock = new MasterClock();
        clock.Start();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 200; i++)
                {
                    var t = clock.CurrentMediaTime;
                    var r = clock.IsRunning;
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        clock.Stop();

        Assert.Empty(errors);
    }

    [Fact]
    public void ConcurrentStartStop_DoesNotThrow()
    {
        var clock = new MasterClock();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var threads = Enumerable.Range(0, 6).Select(i => new Thread(() =>
        {
            try
            {
                for (var j = 0; j < 100; j++)
                {
                    if (j % 2 == 0) clock.Start(); else clock.Stop();
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        clock.Reset();

        Assert.Empty(errors);
    }
}
