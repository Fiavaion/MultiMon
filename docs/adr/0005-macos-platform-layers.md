# ADR 0005 — macOS platform layers: the stack actually chosen

**Status:** ACCEPTED (2026-09-09, pending the TODO 9 ship gate) · **Milestone:** `mac-port` TODO 1–8
**Context:** [ADR 0001](0001-persistent-d3d11-rebuild.md) (persistent pipeline, **no GPU abstraction layer**),
[ADR 0002](0002-m3-sync-and-multi-adapter-present.md) (MasterClock + frame selection),
[ADR 0003](0003-m7-mode-mapping-and-control-firewall.md) (UV sub-rects, control firewall);
[docs/MAC_PORT.md](../MAC_PORT.md) (the seams), [docs/MAC_SETUP.md](../MAC_SETUP.md) §6 (the TODO gates).

This ADR **amends 0001, it does not supersede it.** `MultiMon.Graphics.Mac` is a **sibling platform assembly
behind the same Core abstractions**, not a Metal backend under an abstraction over D3D11. No interface spans a
`ID3D11Device` and an `MTLDevice`; the shared halves are `MultiMon.Core` and `MultiMon.Hap` only, both still
free of platform types. `MultiMon.sln` is never edited from the Mac; `MultiMon.Mac.sln` builds six
`net9.0-macos` siblings plus the two portable assemblies.

Evidence in each decision is the measured gate from the `mac-port` commit that landed it
(`git log a5f2985..HEAD`); all Mac gates below ran on a **single-display, single-audio-device, Apple Silicon**
box, which is what D9 bounds.

## Decisions

### D1 — Bindings: Microsoft.macOS (`net9.0-macos` workload), not SharpMetal
The official Microsoft.macOS bindings cover AppKit, Metal, CoreVideo, VideoToolbox, AVFoundation, CoreAudio and
AudioUnit in one supported surface. SharpMetal (preview, Metal only) was rejected: it would leave every other
framework hand-P/Invoked, i.e. exactly the hand-rolled-COM surface 0001 rejected on Windows. Where a binding is
incomplete we P/Invoke the *specific* missing C function rather than adopt a second binding set — two cases
only: `CGDisplayCopyDisplayMode`/`CGDisplayModeGetRefreshRate` (D2) and the three
`AudioObjectGetPropertyData` entry points (D7).

Two workload consequences accepted and worked around, not fought:
- The `net9.0-macos` **workload pins an Xcode version** (workload 26.5 pins Xcode 26.5; this box has 26.6), so
  every app-bundle project carries `ValidateXcodeVersion=false`.
- `net9.0-macos` executables build as **`.app` bundles and `dotnet run` does not relay their stdout** — every
  harness invocation runs the bundle binary directly
  (`MultiMon.Stress.Mac/bin/…/MultiMon.Stress.Mac.app/Contents/MacOS/MultiMon.Stress.Mac`). This is a
  documented command-map difference, not a harness change.
- Evidence: `cb81749` — scaffold builds 0 errors / 0 warnings, `MultiMon.Core.Tests` 126/126 with `MultiMon.sln` untouched.

### D2 — Monitors: `NSScreen`, `DeviceId` = `CGDirectDisplayID`, pixel coordinates by the primary's scale
`MultiMon.Platform.Mac/MonitorService` implements `IMonitorService` over AppKit.
- `MonitorInfo.DeviceId` = the `CGDirectDisplayID` as a string. It is only ever an **opaque key** (show mapping,
  `ShowPlanner`), which is the whole of the portable contract — the Windows `\\.\DISPLAY1` value is not
  reproducible and nothing needs it to be.
- **Coordinate rule (the load-bearing one):** AppKit reports every screen's frame in points in ONE global space
  (bottom-left origin, Y up); Core wants physical pixels, top-left origin, Y down. Conversion:
  **origins × the PRIMARY screen's backing scale (Y flipped about the primary's top edge); sizes × each
  screen's own backing scale.** Rejected: scaling each origin by its own screen's scale — on a Retina +
  non-Retina pair that puts two monitors in different units, they overlap in the virtual rect and the Span
  union collapses (a `UvLayout` 1×1 degenerate). This was found by the fresh-context verify pass (D10), not by
  a gate, because the box has one display.
- `RefreshRate` = the **current** `CGDisplayMode` rate via the two CoreGraphics P/Invokes above. Rejected:
  `NSScreen.MaximumFramesPerSecond` — it reports the panel maximum, so a 120 Hz panel running at 60 would
  report 120 and mis-drive pacing/frame selection.
- `IsPrimary` = the menu-bar screen; `MonitorsChanged` from `didChangeScreenParametersNotification`.
- Evidence: `0eb922c` — `--list-monitors` gate; two portable `ShowPlanner` tests (the real single-screen
  geometry and a derived mixed-DPI pair); Core.Tests 128/128, Mac solution 0 warnings.

### D3 — Graphics: ONE `MTLDevice`, one persistent `CAMetalLayer` window per screen, one render thread
The 0001 persistence rule ported verbatim. `GraphicsDeviceProvider` is the single ref-counted owner of the
device, its ONE command queue and the shared `QuadPipeline`; `OutputWindow` is one borderless `NSWindow` +
`CAMetalLayer` per screen created ONCE and only shown/hidden per perform; `RenderLoop` is one thread that
exclusively owns every command buffer, encoder and present. Pacing is `displaySyncEnabled` on the layer plus
`nextDrawable` (the vsync equivalent of ADR 0002 D3's `Present(1)`); `FrameTimeline` is copied verbatim so
frame selection stays the ADR 0002 contract.
- **Shaders:** `Quad.hlsl` → MSL, compiled once. The uniform block is padded with **scalars, never a vector**
  (`float3` is 16-byte aligned in MSL and silently shifts every later field — LESSON-API-004; the bug shipped a
  flat texel and every numeric gate still passed because the pattern only reads offset 0). Layout is asserted in
  comments on both sides and gated by pixel readback (`--source-check`).
- **Live-object accounting** (the D3D11 debug layer has no direct twin): `MetalResourceTracker` counts every
  Metal object this assembly creates, **plus** `MTLDevice.currentAllocatedSize` for bytes. Both are needed —
  "tracked flat, allocation growing" is the signature of an unpooled autoreleased object (LESSON-BUG-008), which
  the count alone cannot see.
- **`NSAutoreleasePool` per iteration on every long-lived thread** (render, decode, main pump, harness cycle).
  Without it the first validation run grew ~2 MB/frame, +3 GB over 47 cycles.
- **Main-thread marshalling:** AppKit window ops go through `MainThread.Invoke` with a **bounded** wait (5 s) so
  a stuck main thread surfaces as a loud wedge, never a silent hang; `RenderLoop.Invoke`/`Stop` **throw when
  called on the AppKit main thread** — the V0087 rule made structural rather than documented.
- Evidence: `8339c99` — 50 cycles PASS, tracked 9 flat, alloc flat, RSS +8 MB sub-linear, clean under
  `MTL_DEBUG_LAYER=1`; `--source-check` 5/5; Core.Tests 128/128.

### D4 — Frames: a hold-count fence on `DecodedFrame` for Metal's asynchronous blit
The Windows `FrameTimeline` contract is "the timeline disposes the frame once the render thread has passed it,
and disposal runs the producer's release closure." On Metal the pass's copy is **encoded**, not executed, when
that happens — so disposal alone would recycle a texture the GPU is still reading. `DecodedFrame` therefore
counts holds: one for its owner (dropped by `Dispose`), plus one per encoded read taken by
`HoldUntilCompleted`. The hold is taken **by the committer, immediately before `Commit`** — never by the
encoder, so a command buffer abandoned before Commit (whose completed handler never fires) cannot strand a
hold — and dropped by that buffer's completed handler. The release closure runs on the **last** drop, whichever
side that is; a frame never copied releases on Dispose. This is the single mechanism that makes D5's texture
pool and D6's CVPixelBuffer reuse safe, and it is why the Windows `FrameTimeline` could be copied unchanged.

### D5 — HAP: `MultiMon.Hap` unchanged, `replaceRegion` into a per-source pool of 12 persistent textures
`MultiMon.Hap` already emits BC1/BC3/BC4/BC7, which Metal supports on **all** Macs (Apple Silicon and Intel),
so the HAP path is upload-only — no format decision to re-make. `HapSource` decodes on its own thread and does
one `replaceRegion` per frame into a texture rented from a `TexturePool` created ONCE per source at the clip's
size/format (no per-frame texture creation, LESSON-ARCH-002). Pool size = timeline depth + in-flight margin
(three command buffers per output + the one being written) = 12; when exhausted the decode thread **blocks in
`Rent`, it never overwrites**. Textures return only through the D4 release closure. The pass blits BCn→BCn into
its own persistent texture of the same format and **HAP-Q YCoCg→RGB happens in the shader** — the same
division of labour as Windows, so no CPU colour path exists on either platform.
Frame validation is the Windows rule: exact declared format and exact byte length (a short frame would be a
native over-read), skip-and-log, fault only after a burst.
- **Chroma gate:** the 5 s fixture is greyscale and cannot prove the YCoCg path, so a HAP-Q colour clip
  (`colour_hapq.mov`, ffmpeg `testsrc2`) is the readback fixture; `--source-check` compares the most chromatic
  and brightest texels against a CPU BC3+YCoCg reference and was proven to FAIL on a swapped-chroma probe.
- Evidence: `371d7df` — `--hap --mode=span` 50 cycles PASS under MTL validation (tracked 7 flat, alloc flat,
  sources 50/50/50), 2-source individual PASS, `--source-check` 7/7.

### D6 — Video: AVAssetReader demux + `VTDecompressionSession`, BGRA zero-copy via `CVMetalTextureCache`
`VideoToolboxSource` is the `MediaFoundationSource` twin: `AVAssetReader` hands compressed `CMSampleBuffer`s
(no output settings) to a `VTDecompressionSession`.
- **HW required first, logged SW fallback.** The session is created *requiring* the hardware decoder; on
  failure it is recreated allowing software and the fallback is logged. `--force-sw-decode` skips the hardware
  attempt. The active path is **read back from the session**, never assumed. Known limit: the fallback is
  decided at session creation only (mid-stream HW loss is not yet exercised) — revisit on the TODO 9 Intel pass.
- **BGRA, not NV12.** Output is `kCVPixelBufferMetalCompatibilityKey` BGRA wrapped zero-copy by a
  `CVMetalTextureCache`, giving one format-uniform contract with the Windows pass (BGRA→BGRA blit). An NV12
  output + a YUV→RGB MSL pass was conditional on measurement: unpaced 4K decode measured **412 fps**, so the
  extra pass is unjustified and was not built.
- **Looping by reader recreation** (a reader cannot rewind); the session is reused and **drained**
  (`FinishDelayedFrames` + `WaitForAsynchronousFrames`) at each loop boundary, with the loop base carried per
  sample through VT's `sourceFrame` token, so PTS stay ascending across loops (ADR 0002 D1) — drift is
  corrected by frame selection, never seeking.
- **Timeline depth 4.** Each buffered frame pins a decoder pool buffer (33 MB at 4K BGRA) plus up to three
  in-flight render reads, so the handoff stays shallow; `Publish` blocking on a full timeline is what paces
  decode to real time.
- **Colour:** the YCbCr matrix conversion is left to VideoToolbox; the stream's tagged matrix and the matrix VT
  actually applied to the buffer are both logged. VT applies bt709 to untagged clips (observed on this box, per
  the TODO 5 note in `notes/CURRENT_STATUS.md`) — accepted, not overridden.
- **Symmetric ladder** (LESSON-TEST-004): `SourceLadder` probes a `.mov` as **HAP first** and falls through to
  VideoToolbox; `--hap` refuses the fallthrough so a HAP gate can never silently pass on the other decoder
  (a false-green found by the verify pass, D10).
- Evidence: `8136cc5` — individual 50 cycles × 2 clips under MTL validation PASS (tracked 8 flat, alloc flat,
  sources 100/100/100), 4K H.264 and 4K HEVC 30 fps sustained on hardware, software path PASS,
  `--source-check` 8/8 (VT readback against a bt709 limited-range reference).

### D7 — Audio: AUHAL per track (not AVAudioEngine), CoreAudio property P/Invokes, polled device rebuild
- **AUHAL, not AVAudioEngine.** AVAudioEngine cannot be pointed at a specific output device;
  `kAudioOutputUnitProperty_CurrentDevice` on an AUHAL unit can, which `AudioTrack.OutputDeviceId` requires. The
  AUHAL render callback is also the direct analogue of the WASAPI fill, so `WasapiOutput`'s one-shot
  proportional drift policy (drop ring frames when behind / insert silence when ahead, never reseek the
  decoder) is a **line-for-line port** rather than a re-derivation. One unit per track; several may share a
  device and the HAL mixes them, exactly as shared-mode WASAPI does. The unit's input scope is the decoder's
  native interleaved-float format and AUHAL converts on the way out (the `AUTOCONVERTPCM` contract), so the
  decoder never resamples.
- The render callback is a **fenced realtime entry point**: no locks, no allocation, no logging, no ObjC (hence
  no autorelease pool); it touches only the ring, volatile mixer values and the clock, reports by flag, and
  always returns `NoError`. A watch thread logs the flags.
- **Device ids are UIDs.** `AudioOutputDevice.Id` = `kAudioDevicePropertyDeviceUID` (stable across sessions,
  the Windows IMMDevice-id equivalent); raw `AudioObjectID`s are re-issued on re-plug and never leave
  `AudioDeviceEnumerator`. The managed bindings mark `AudioObjectPropertyAddress` and its selectors
  **internal** with no `AudioObjectGetPropertyData` entry point, so the struct is mirrored and three functions
  are P/Invoked here (the D1 exception).
- **Device-invalidated recovery is polled, not a property listener.** A CoreAudio property listener would put a
  native callback's lifetime in the middle of teardown; a polled liveness check on the watch thread rebuilds
  with the Windows sequencing and has no callback to unregister on a device that is already gone.
- **`AudioRing` moved to `MultiMon.Core/Audio`** (pure C#), namespace kept as `MultiMon.Audio` so both platform
  engines compile unchanged, and its 13 tests moved to `MultiMon.Core.Tests`. This is **D-006**: statically it
  compiles (both Windows projects already reference Core) but **one `dotnet build MultiMon.sln` +
  `dotnet test MultiMon.Tests` on Windows is owed and blocks `mac-port` → `master`.**
- **The audio gate asserts content and signal** (LESSON-TEST-005): a gate that measured only drift and
  underruns PASSed 30 cycles with **zero** render callbacks — a wedged `coreaudiod` read as "perfect".
  `HoldForContent` now requires ≥ 250 ms of rendered content **and** a post-gain peak ≥ 0.01 (a dBFS floor, since
  MP3 dither meters 0.0001), bounded by a 2 s lead-in allowance for encoder-delay silence;
  `--audio-gain=0` / `--audio-master=0` skip the signal requirement. Diagnosis outcome: no bug in
  `MultiMon.Audio.Mac` — the zero-callback state was the machine's audio daemon; keep the box awake.
- Evidence: `5ee0333` — mp3 30 cycles peak drift 2.3 ms, clip+AAC 30 cycles 2.4 ms, both 0 underruns;
  `39319e1`/`97894d1` — mp3 30 cycles PASS peak 0.358, drift 1.3 ms, 0 underruns; `--audio-gain=0.5` → peak
  0.179; `--audio-pan=-1` → peakR 0.000.
- **D-007** stands: device selection by UID, the invalidated rebuild and multi-device mixer routing have no
  gate on a one-device box — TODO 9 hardware.

### D8 — Controller: the Windows shape, split so the harness carries no Avalonia
`MultiMon.Control.Mac.Core/PerformanceController` is `IPerformanceController` shape-for-shape: ONE long-lived
worker thread with a `BlockingCollection` FIFO, every public call posts and returns, `StateChanged` /
`CommandFailed` raised on the worker (handlers marshal). Device, render loop and one persistent `OutputWindow`
per monitor are built once in the constructor and never rebuilt; `ApplyShow` runs `ShowPlanner` (no port
needed) and the D6 ladder; `ExitPerform` only hides. **The constructor and `Dispose` refuse the AppKit main
thread** — a host builds and disposes on a background thread. Teardown drains in-flight command buffers, then
stops/joins/disposes sources and audio in the CLAUDE.md order. Master volume/mute is owned here (LESSON-BUG-007).
`EnterPerform` rolls back partially — a mid-loop failure hides every output before rethrowing, so state stays
Idle with nothing on screen.
- **The split** (`Control.Mac.Core`, `net9.0-macos`) exists so the Stress bundle contains **zero Avalonia
  files**: the harness must exercise the app's real controller without inheriting the UI framework's runtime.
- **Free-run clocks on the gate:** `--free-run` carries the Windows semantics and the controller path FAILs if
  the planner bound a different clock count.
- **One `Watchdog` helper covers cycles AND teardown on both harness paths** (bind-once `loop.Stop` →
  `provider.Dispose` as well as `controller.Dispose`); a deadline miss dumps stacks and exits 1 as
  "teardown wedge" — proved with a temporarily induced render-thread stall.
- Evidence: `4421dfd` — controller path pattern 50, span 50×2, individual 50×2 (also under MTL validation,
  tracked 20 flat), split 30, hap span 50, audio 30, all PASS, Idle every cycle, 0 CommandFailed;
  `505c76b` — individual `--free-run` 50×2 PASS (tracked 20 flat), split 50, span 20, induced teardown wedge →
  FAIL exit 1 in 11 s.

### D9 — UI host: Avalonia 12 coexisting with Microsoft.macOS, shared view-model, ordered exit
- **Coexistence, proved by spike before building on it:** `NSApplication.Init()` must run **first** on the main
  thread, after which Avalonia's run loop **drains the main dispatch queue** — which is exactly what D3's
  `MainThread.Invoke` needs. `Control.Mac` must be `net9.0-macos` (it references `Graphics.Mac`), so this
  coexistence was the gating unknown, not a preference.
- **`MainViewModel` + `ConvertToHapViewModel` + `FfmpegHapConverter` copied into `MultiMon.Control.Shared`**
  (`net9.0`, platform-free) behind an **`IUiDispatcher`** seam — the Dispatcher was the only WPF dependency.
  Windows is untouched, so the two copies are **duplication owed a dedupe on the Windows pass** (tracked in
  `notes/TODOs.md`, TODO 8 decisions). Core `FileLog` resolves `~/Library/Logs/MultiMon` via
  `OperatingSystem.IsMacOS` only.
- **The exit path (LESSON-BUG-009):** `desktop.Shutdown()` does **not** honour `e.Cancel` (only OS-initiated
  quits do), so cancelling `ShutdownRequested` and tearing down afterwards ended the run loop first and left
  `controller.Dispose` → render-thread join → `OutputWindow.Hide` → `MainThread.Invoke` waiting on a queue that
  would never drain again — the V0087 deadlock, reproduced at app exit and confirmed with `sample`. The
  decision: ONE idempotent `RequestExitAsync(exitCode)` that tears down on a background thread the UI thread
  **awaits while the run loop is still alive** (10 s bound, logs `teardown complete: live=0`), and only then
  calls `desktop.Shutdown()`. Window close and Cmd+Q both route through it; `ShutdownMode =
  OnExplicitShutdown`. The harness never saw this because it pumps AppKit by hand to the very end — hence the
  rule: test app exit with a real launch (`--autoperform`), not only the harness.
- **Mac-specific product choices** (orchestrator, 2026-09-09; revisit at TODO 9 sign-off): hotkeys
  (F11/Esc/Space) are **frontmost-only** (true global would need Carbon `RegisterEventHotKey`); **no
  Report-a-bug** button pending a Mac diagnostics gatherer; **HAP is always enabled** on Metal (BC1/3/4/7 are
  universal, so there is no `GpuCapabilityService` twin and no D-004-style capability gate); the monitor list is
  an **`ItemsControl`, not a DataGrid** (Avalonia's DataGrid is a separate package for a list this app only
  ever renders as rows). Convert-to-HAP is code-complete but no real conversion has been run on macOS.
- Evidence: `0d546bc` — build 0/0, Core.Tests 141, **harness bundle contains 0 Avalonia files**, controller
  free-run 20×2 PASS, `--autoperform` → Performing → Stopped → teardown live=0 → exit 0, project round-trip
  byte-identical.

### D10 — Verification model: the same two-half gate, and what this box cannot prove
- The Windows rule carries over unchanged: a checkpoint is green only when **both** the bind-once path and the
  `--controller` path pass (LESSON-TEST-004), at the same cycle counts, with zero wedge, flat tracked count,
  flat allocation and flat RSS. Modes gated: span, split, individual, HAP, audio.
- Runs are made under **`MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1` as a matter of course** — validation
  inflates per-object cost and makes a slow native leak visible in one run (LESSON-BUG-008).
- **Not verifiable on this box, deferred to the TODO 9 matrix:** multi-screen span/sync and window placement;
  the mixed-DPI origin rule (D2, unit-tested from derived values only); audio device selection, device
  switch and mid-show unplug (D-007); an **Intel Mac** (a second GPU class, and the only place D6's
  HW→SW fallback is exercised for real); and the **Windows build of `MultiMon.sln`** for the `AudioRing` move
  (D-006). None of these are code-open questions; all are hardware gates.
- **Process finding, recorded because it changes routing:** the fresh-context `verify` agent caught **four real
  defects that every green gate had missed** — the mixed-DPI origin overlap (D2), the MSL vector-padding
  offset shift (D3/LESSON-API-004), a false-green `--hap` run that had silently fallen through to the other
  decoder (D6), and a vacuous audio gate that passed with zero render callbacks (D7/LESSON-TEST-005). Three of
  the four are "the gate proved nothing about the path it claimed to prove." A fresh-context verify pass on each
  native slice is therefore part of the Mac workflow, not an optional extra; where it was skipped (TODO 5, by
  user choice) that is noted in `notes/CURRENT_STATUS.md`.

## Consequences
- The Mac and Windows halves share only `MultiMon.Core` + `MultiMon.Hap`; every native concern is a sibling
  assembly. ADR 0001's no-abstraction-layer rule survives the second platform intact.
- Three fences make the Mac core safe where Windows needed none: D4's hold count (asynchronous blit), D3's
  per-iteration autorelease pools (ObjC autorelease), and D3's main-thread refusals (AppKit affinity).
- Two debts are open against `mac-port` → `master`: **D-006** (Windows build owed) and **D-007** (audio paths
  ungated). One duplication is owed a dedupe (D9, `Control.Shared` vs the WPF view-models).
- Status stays **pending the TODO 9 ship gate**: the full matrix on Apple Silicon **and** one Intel Mac, plus
  the user's manual run of every mode (the TODO 8 gate, still open).

## D11 — Match display refresh rate to the clip while performing (added 2026-09-13)

**Decision:** on `EnterPerform`, before the output windows are shown, each output's display is switched (via
`CGConfigureDisplayWithDisplayMode`, `kCGConfigureForAppOnly`) to the highest available mode with the **same pixel
and point size** whose refresh rate is an integer multiple of the bound source's frame rate (|rate − k·fps| ≤ 0.06 Hz);
restored after the hides on `ExitPerform`, on a failed Show, and at Dispose. Off switch: `MatchDisplayRefresh`
(panel checkbox, app-level). Mac-only; no Windows parity yet.
**Why:** 25 fps material on a 60/120 Hz cadence is an uneven 2-3 hold pattern the user saw as stutter in Span; holding
pixel size means the persistent windows never move or resize (`MonitorsChanged` fires, bounds are identical, nothing
rebuilds). **Evidence:** span 25 fps on both displays → 120→50 Hz and 60→50 Hz, 781 presents / 15.4 s per output
(every vsync, each frame twice), restored every cycle, 10 cycles tracked flat. This panel has no 100 Hz mode at the
current scaling, so 50 Hz is the honest best. `kCGDisplayShowDuplicateLowResolutionModes` must be the exported
symbol (`Dlfcn.GetStringConstant`), not a same-spelled NSString — the latter is silently ignored.

**D6 amendment (2026-09-13):** VideoToolbox's hardware decoder invokes the output callback in **decode order**
regardless of `kVTDecodeFrame_EnableTemporalProcessing` (measured: 20/40 inversions on a B-frame clip with and
without the flag). `VideoToolboxSource` therefore holds a sorted reorder window (`ReorderDepth = 4`; measured need
3) and publishes the smallest PTS; `FrameTimeline.Publish` now refuses a non-ascending PTS so a deeper stream
faults loudly instead of the selector silently picking stale frames. `ReorderDepth = 4` is an **empirical bound**,
not a derivation from the codec: the fixture survey (ffmpeg-default H.264/HEVC, 3 B-frames) needed depth 3, and 4 is
that plus one of margin; a stream with deeper reordering faults the source (the guard) rather than stuttering. Note
also that `Drain` moves the whole burst VideoToolbox emitted since the last drain into the window before publishing,
so the effective window is max(4, burst) — a burst never shrinks it. `--source-check` check 9 is the gate for this
(60 distinct frames of the B-frame fixture, consumed like the render thread, strictly ascending; it fails with the
guard and the window disabled); its two clips are required — `--video`/`--video2`, else `MULTIMON_HAP_FIXTURE`/
`MULTIMON_H264_FIXTURE`, else the media-folder defaults `Hap/colour_hapq.mov`/`colour_h264.mp4` — and a missing clip
FAILS the run instead of skipping the check. Before this, 92 % of presents on H.264 selected a stale frame — the
stutter D11 was chasing was mostly this. **D11 note:** immediately after a display mode switch
the freshly shown layer presents unthrottled for a few frames (30 presents in 16 ms observed); the harness dwell is
now ≥300 ms per cycle so the advance check is meaningful.
