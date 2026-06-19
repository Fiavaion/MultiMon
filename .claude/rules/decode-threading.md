---
description: Decode threading — Media Foundation / HAP decode off the UI thread, triple-buffer handoff, frame-select for drift.
paths:
  - "MultiMon.Decode/**"
---

# Decode Threading Rules

Decode is where foreign threading models historically corrupted state. Keep it isolated and lock-free.
Delegate non-trivial native decode/handoff work to the **`stability-core`** agent (Fable).

## Decode runs off the UI and render threads (MANDATORY)
- Each `ISource` (`MediaFoundationSource`, `HapSource`) runs its own decode loop on its own thread.
- Decode NEVER runs on the UI thread or the render thread. The UI thread only issues commands
  (`LoadClip`/`EnterPerform`/`ExitPerform`/`Seek`) via the concurrent command queue.

## Lock-free triple-buffer handoff
- A decode thread publishes the latest-ready GPU texture into a triple buffer; the render thread reads
  the most-recent frame for its target present time. Frames handed across are immutable once published.
- The render thread only READS. No shared mutable texture is written by two threads. Validate with the
  D3D11 debug layer in the harness (cross-thread texture race = crash/tear).

## Never seek a live decoder (drift is corrected by frame selection)
- Drift between a source and the `MasterClock` is corrected by **frame select (drop/repeat)** via
  `FrameSelector`, NOT by seeking. Seeking a live decoder was a native-crash trigger in the old app.
- Each source decodes ahead and selects, per present, the frame whose PTS is closest to
  `MasterClock.CurrentMediaTime` (+ per-clip `TimeOffset`).

## Media Foundation: bind to OUR device, HW decode with automatic SW fallback
- `MfDeviceManager` binds MF to the single `ID3D11Device` (`IMFDXGIDeviceManager`) for hardware decode.
- If HW decode is unsupported/fails on a GPU, fall back to MF **software** decode automatically — same
  texture output path. NEVER crash; log + fall back (LESSON-PERF-001 generalized).

## HAP: from scratch, gated by GPU capability
- HAP path = vendored MOV atom demux → `HapContainerParser` → BCn texture upload → `HapYCoCg.hlsl`.
  No Veldrid, no third-party HAP dependency.
- HAP is an **enhancement**: gate it with the salvaged `GpuCompatibilityService` blacklist (BC7/feature
  capability). If unsupported, fall back to MF decode of an H.264 proxy or skip — never crash.

## Verification gate
Decode changes are verified by `MultiMon.Stress` with `--hap`/mixed sources: 50/50 clean cycles, no wedge,
content-sync drift within one frame (logged). Unit-test the pure logic (`HapContainerParser`,
`FrameSelector`) in `MultiMon.Tests`, but the harness is the real gate.
