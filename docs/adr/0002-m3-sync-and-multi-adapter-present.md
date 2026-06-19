# ADR 0002 — M3: master clock, frame selection, and multi-adapter / mismatched-refresh present

**Status:** ACCEPTED (2026-06-10) · **Milestone:** M3 → Checkpoint B
**Context:** [REBUILD_ARCHITECTURE.md](../../REBUILD_ARCHITECTURE.md) §2.3, §4 (Checkpoint B); supersedes nothing (extends 0001).

## Context

M2 shows the *latest decoded* frame with no timeline — playback speed equals decode speed. M3 introduces
time-correct playback across N monitors. The dev box is the worst-case Checkpoint B rig: two outputs on
**different adapters** (NVIDIA RTX 3080 @ 1920×1080 **60 Hz** primary; AMD Radeon iGPU @ 2560×1440
**165 Hz**). The D3D11 device is created on the primary adapter (NVIDIA), so the AMD output's swapchain is
a cross-adapter present.

## Decisions

### D1 — MasterClock drives playback (QPC monotonic timeline)
A single `MasterClock` (QueryPerformanceCounter) exposes `CurrentMediaTime`, accumulated across
start/stop. Sources decode ahead; the render thread selects, per present, the frame whose PTS best matches
`CurrentMediaTime` (+ per-clip offset). **Drift is corrected by frame selection (drop/repeat), never by
seeking a live decoder** (decode-threading.md). Looping maps absolute media time into clip time by the
clip duration.

### D2 — Frame handoff: latest-only → bounded PTS-ordered buffer
The M2 `TripleBuffer` (latest-only) cannot answer "the frame for time T". Replace its *consumption* with a
small **bounded PTS-ordered frame buffer** (`FrameTimeline`): the decode thread publishes frames in PTS
order; the render thread asks `FrameSelector` for the best frame for the target time and releases frames
it has passed. Bounded depth (a few frames) caps held decoder-pool textures. Still single-producer/
single-consumer and lock-light; the render thread only reads + releases.

### D3 — Present pacing: ONE render thread, `Present(1)` per swapchain (the fork)
The single render loop iterates all swapchains and calls `Present(1)` (vsync) on each. With 60 Hz + 165 Hz
the loop paces at roughly the combined/slower cadence, so the 165 Hz monitor presents below its max — **but
both monitors select the temporally-correct frame for the SAME `MasterClock` time, so they stay content-
synced within one frame regardless of present rate.** Checkpoint B's bar is *content-sync*, not peak
refresh.

- **Rejected: per-monitor present threads.** Only one immediate context exists; concurrent context use
  serializes under the multithread lock anyway, so parallel present needs deferred contexts/command lists —
  complexity unjustified for the sync bar.
- **Rejected (for now): waitable swapchains** (`FRAME_LATENCY_WAITABLE_OBJECT` + `Present(0)`). Revisit
  ONLY if the slower-paced loop fails the sync/throughput bar at Checkpoint B. Test decides, not speculation.

### D4 — Multi-adapter: single device + DWM cross-adapter present
`HMonitorToAdapter` maps each output HMONITOR → its `IDXGIAdapter` (match `IDXGIOutput.DesktopCoordinates`).
`AdapterSelector` creates the device on the adapter driving the primary/most outputs. Outputs on *other*
adapters use the **same device**; the DWM composites the flip-model swapchain cross-adapter. **The
per-adapter-device split stays deferred** (the §8 Q3 test-gated decision) — build it only if Checkpoint B
shows garbage/perf failure on this AMD+NVIDIA box. Each run logs the HMONITOR→adapter map and which
outputs are cross-adapter.

## Public contracts (both the impl and the parallel test suite code to these)

```csharp
// MultiMon.Core/Timing/MasterClock.cs — QPC monotonic media timeline, accumulates across pause.
public sealed class MasterClock
{
    bool IsRunning { get; }
    TimeSpan CurrentMediaTime { get; }   // 0 until first Start; advances only while running
    void Start();                        // begin or resume from the held time
    void Stop();                         // pause; CurrentMediaTime holds its value
    void Reset();                        // stop and return CurrentMediaTime to zero
}

// MultiMon.Core/Sync/FrameSelector.cs — PURE: pick the frame to show for a target time.
public static class FrameSelector
{
    // framePts is ascending. Returns the index of the frame with the greatest Pts <= targetTime;
    // if targetTime precedes all frames, returns 0; empty list throws ArgumentException.
    // Pure and allocation-free — the unit of drift correction (drop/repeat by index).
    static int Select(IReadOnlyList<TimeSpan> framePts, TimeSpan targetTime);
}
```

## Verification (Checkpoint B)
`MultiMon.Stress --cycles=50 --windows=2 --fullscreen --video=<clip>` on the AMD+NVIDIA box:
50/50 cycles, zero wedge, **zero D3D11 live-object growth**, clean exit; **content-sync drift ≤ 1 frame**
between outputs (logged); per-output `--capture` confirms both outputs show the correct frame. HMONITOR→
adapter map + cross-adapter outputs logged. Adjust trigger: any monitor wedges or desyncs > 1 frame →
present/clock bug; cross-adapter garbage → escalate to per-adapter devices (D4).

---

## Addendum (2026-06-11, M6) — D3 fallback ACTIVATED: waitable swapchain + `Present(0)`

**Status:** D3's `Present(1)` decision is superseded by its own pre-registered fallback. The test predicted
by D3 ("revisit ONLY if the slower-paced loop fails the … bar") has now failed, so the fallback is adopted.

**Trigger / evidence.** M6 audio surfaced an intermittent render-thread wedge: both cross-adapter outputs
stop presenting for 5 s+, frozen inside `Present`, with **no `DXGI_ERROR_DEVICE_REMOVED`** (so not a TDR).
Bisected with a `--raise-timer` harness switch (`timeBeginPeriod(1)`, no audio): raising the **global system
timer resolution to 1 ms** — exactly what Media Foundation / WASAPI do process-wide while audio plays —
reproduced the wedge in **~50 % of HAP-only runs** (vs 0/9 at the default timer). Root cause: serial
`Present(1)` (vsync) across two **mismatched-refresh (60 Hz + 165 Hz), cross-adapter** flip-model swapchains
blocks indefinitely on the DWM when the 1 ms timer shifts the composition cadence. Audio was only the
trigger, not the cause — it is a present-model defect.

**Decision.** Each output swapchain is created with `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT` +
`SetMaximumFrameLatency(1)`. The render loop, once per iteration, blocks **with a bounded 100 ms timeout** on
the visible swapchains' frame-latency objects (`WaitForMultipleObjects`, `waitAll`) for pacing/back-pressure,
then presents each with **`Present(0)`** (queue-and-return; the flip-model DWM still composites at vblank, so
no tearing — no `ALLOW_TEARING`). The bounded wait means the present path can never freeze for seconds; a
stalled compositor costs at most a dropped beat. Pacing is still the slowest visible refresh (`waitAll`), so
D3's content-sync property is unchanged — cross-monitor sync remains by **frame selection at one clock
sample**, independent of present rate.

**Regression gate.** `--raise-timer` is retained as a permanent harness switch: HAP-only,
`--cycles=50 --windows=2 --fullscreen --raise-timer` must pass 50/50 with zero wedge after any
render/present change (it was ~50 % wedge before this addendum).

**Unchanged:** D1 (MasterClock), D2 (FrameTimeline), D4 (single device + DWM cross-adapter present). Per-
monitor present threads are still rejected (one immediate context). Lesson: see LESSON-PERF-002.
