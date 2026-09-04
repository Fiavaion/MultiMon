# MultiMon Rebuild — Architecture ADR + Build Plan

**Status:** PROPOSED (drives bootstrap of the new solution)
**Date:** 2026-06-10
**Author:** architecture planner
**Supersedes:** the entire WPF + LibVLCSharp.WPF implementation (backed up at `Old/MultiMon_LibVLC_V0087/`)

> Acceptance test (the ONLY one that matters): a stable multi-monitor video wall that runs reliably
> on AMD, NVIDIA, Intel, and integrated GPUs across 50+ open/close cycles with zero leak, crash, or
> wedge. **Stability above all else.**

---

## 1. ADR — Persistent D3D11 pipeline, thin maintained OS bindings, no GPU abstraction layer

### 1.1 Context (why the old app cannot be patched)

The old MultiMon embedded per-cycle LibVLC `MediaPlayer`s inside WPF `VideoView` controls. Two full
crash dumps + the headless `StressHarness.cs` settled the root cause after 18 surface fixes failed:

- **UI-thread teardown deadlock.** `libvlc_media_player_stop` / `libvlc_event_detach` block until the
  D3D9 vout thread finishes; that vout thread needs the **UI message loop** to complete shutdown. Run
  teardown on the UI thread → self-deadlock. The fullscreen secondary-monitor vout never tears down
  cleanly; the small windowed one does — which is exactly why the harness's primary-monitor run was
  always clean (60 lifecycles, zero wedge) while real 2-monitor perform mode wedged by ~cycle 5.
- **Per-cycle native churn.** Creating and destroying decoders + D3D9 vouts every perform cycle leaked
  DXVA2/D3D9 state in the shared instance, eventually wedging the second concurrent decoder in Opening.

The lessons (`notes/lessons.md`, `LESSONS_LEARNED.md`, `CRASH_INVESTIGATION.md`) that the rebuild must
NOT repeat:

| Lesson | Rebuild rule it produces |
|--------|--------------------------|
| LESSON-ARCH-001: native libs need a single owner | ONE D3D11 device, owned by one provider, ref-counted |
| LESSON-BUG-001: delays mask, never fix native crashes | NO `Thread.Sleep`/GC.Collect as a "fix"; fix ownership |
| LESSON-ARCH-002: periodic reset made it worse | NO pipeline rebuild per cycle; build once, reuse forever |
| Teardown deadlock (V0087) | NO synchronous native GPU/decode/teardown call on the UI thread, ever |
| LESSON-BUG-003: Stop before Dispose | Decoder/session lifecycle has an explicit ordered teardown |
| LESSON-PERF-001: HAP needs modern HW | HAP is an enhancement with a guaranteed software-decode fallback |

### 1.2 Decision

1. **Persistent D3D11 pipeline.** ONE `ID3D11Device` (+ immediate/deferred contexts), and ONE
   `IDXGISwapChain1` per monitor, created ONCE at app start and reused for the whole session.
   Entering/leaving perform mode **swaps content and shows/hides windows** — it NEVER creates or
   destroys the device, swapchains, shaders, or pipeline state objects. This kills both old root
   causes: no per-cycle native churn, and no per-cycle teardown to deadlock on.
2. **Thin maintained OS bindings via Vortice.Windows** (decision + rationale in 1.3). No hand-rolled
   COM. No GPU abstraction layer (Veldrid is abandoned).
3. **HAP built from scratch:** a ~100-line vendored HAP container parser + BC1/BC3/BC7 compressed
   texture upload + a HapQ YCoCg→RGB pixel shader (math referenced from the old Veldrid renderer).
   ZERO third-party HAP dependency.
4. **H.264/HEVC/etc. via Media Foundation → D3D11 texture** (`IMFDXGIDeviceManager` bound to our
   device for hardware decode; automatic software-decode fallback). **LibVLC is dropped entirely.**
5. **Output windows are bare D3D11 swapchain windows** (Win32 `HWND`, no WPF content). The control
   UI is a SEPARATE thin WPF layer that NEVER embeds video and NEVER shares a thread with rendering.

### 1.3 Stack decision — Vortice.Windows vs TerraFX.Interop.Windows

**DECISION: Vortice.Windows.**

| Criterion | Vortice.Windows | TerraFX.Interop.Windows |
|-----------|-----------------|--------------------------|
| Abstraction level | Hand-curated .NET-idiomatic wrappers over D3D11/DXGI/D3D12/**Media Foundation**/XAudio2/D2D | Raw 1:1 auto-generated bindings to the Windows headers |
| API ergonomics | `ID3D11Device`, `IDXGISwapChain1` as ref-counted .NET objects with `.Dispose()`; results via exceptions/`Result` | Raw COM pointers, manual `IID`/`QueryInterface`/`AddRef`/`Release`, `HRESULT` ints |
| Media Foundation | Covered (`Vortice.MediaFoundation`) — keeps decode in the same idiom as render | Present but raw; far more marshalling to write by hand |
| Crash-surface | Lower — fewer hand-written interop lines = fewer places to get a marshalling AV | Higher — every COM call is hand-managed; an `AddRef`/`Release` mistake is a native crash |
| Maintenance | Active, widely used for D3D11 in .NET | Active, lower-level audience |

**Rationale:** stability is the only acceptance bar, and the dominant historical failure mode was
native crashes from interop/ownership mistakes. Vortice's ref-counted `IDisposable` wrappers and
its first-class Media Foundation coverage minimize hand-written interop — the exact surface where the
old app died. TerraFX's raw bindings give marginally more control we do not need and multiply the
hand-managed COM lifetime calls (`AddRef`/`Release`/`QueryInterface`) that are the most common source
of native AVs. We are explicitly NOT hand-rolling COM (constraint), which is what TerraFX nudges
toward. Vortice is "marshalling over Microsoft's own D3D11/DXGI/MF" — exactly the mandated layer.

### 1.4 Alternatives rejected

- **Patch the LibVLC app** — rejected; the deadlock is structural (embedded vout + UI-thread teardown).
- **Veldrid (the old HAP layer)** — rejected per constraint; abstraction hid D3D11 device/swapchain
  lifetime we must own and control explicitly (multi-adapter, device-removed).
- **TerraFX raw bindings** — rejected; more hand-managed COM = more crash surface (1.3).
- **WPF `D3DImage`/airspace interop for output** — rejected; reintroduces the WPF-render-thread
  coupling and airspace issues that the old VideoView embedding suffered.
- **Keep LibVLC for H.264 only** — rejected; the native lib + its vout threading is the thing we are
  removing. Media Foundation decodes to our own D3D11 texture with no foreign vout/threading model.
- **WinUI 3 for the control panel** — viable but heavier bootstrap; WPF is sufficient for a thin,
  video-free control panel and the team knows it. (Logged as OPEN QUESTION Q1.)

---

## 2. Architecture

### 2.1 Component diagram (text)

```
                          ┌──────────────────────────────────────────────┐
        UI THREAD         │  MultiMon.Control (WPF, video-free)            │
        (control only)    │   MainWindow / ViewModels / MonitorAssignUI    │
                          │   ProjectService (save/load .mmproj)           │
                          └───────────────┬────────────────────────────────┘
                                          │ commands (thread-safe, marshalled)
                                          ▼
                          ┌──────────────────────────────────────────────┐
                          │  PerformanceController  (orchestrator)         │
                          │   - owns the master clock                      │
                          │   - maps a "show" onto outputs                 │
                          └───┬───────────────┬───────────────┬────────────┘
                              │               │               │
          ┌───────────────────▼──┐  ┌─────────▼───────┐  ┌─────▼────────────────────┐
RENDER    │ GraphicsDeviceProvider│  │ OutputWindow[ ] │  │ MasterClock              │
THREADS   │  (ONE ID3D11Device,   │  │  bare HWND +    │  │  (QPC-based present time)│
          │   ref-counted, multi- │  │  IDXGISwapChain1│  └──────────────────────────┘
          │   adapter aware)      │  │  per monitor    │
          └───────────┬───────────┘  └─────────────────┘
                      │ shared device / textures
        ┌─────────────▼───────────────────────────────────────────────┐
DECODE  │ ISource (one per loaded clip)                                 │
THREADS │   ├─ MediaFoundationSource  (H.264/HEVC → NV12/RGBA D3D11 tex)│
        │   └─ HapSource              (HAP container → BCn D3D11 tex)   │
        │   each runs its own decode loop OFF the UI thread,            │
        │   publishes the latest GPU texture into a triple buffer       │
        └───────────────────────────────────────────────────────────────┘

AUDIO   ┌───────────────────────────────────────────────────────────────┐
THREAD  │ AudioEngine (WASAPI shared-mode via Vortice / NAudio-free)     │
        │   per-output-device render thread; clocked to MasterClock      │
        └───────────────────────────────────────────────────────────────┘
```

### 2.2 Threading model (this is where stability lives)

- **UI thread:** WPF control panel ONLY. Issues commands (`LoadClip`, `EnterPerform`, `ExitPerform`,
  `Seek`). It NEVER touches a D3D11 device, decoder, or swapchain, and NEVER blocks on a native
  teardown. Commands are posted to the render/decode threads via a concurrent queue.
- **Render/present thread(s):** one dedicated render loop. It owns all `ID3D11DeviceContext`
  rendering and `Present`. Per-monitor present can be one loop iterating all swapchains (simplest,
  preferred for sync) — see 2.3. Created once; lives for the whole session.
- **Decode threads:** one per active `ISource`. Media Foundation / HAP decode happens here, never on
  UI or render thread. Decoded GPU textures are handed to the render thread via a lock-free triple
  buffer (publish latest-ready; render thread reads most-recent for its target present time).
- **Audio thread(s):** one WASAPI render callback thread per output device, fed from a ring buffer,
  clocked off the MasterClock.
- **Teardown:** because the device/swapchains are NEVER destroyed per cycle, the only teardown is at
  app exit, and it runs by (a) stopping decode loops, (b) joining render/decode/audio threads, (c)
  disposing sources, then (d) disposing the device — all OFF the UI thread. No synchronous native
  teardown is ever invoked from the UI thread (the V0087 deadlock rule).

### 2.3 Synchronization (master clock + present timing)

- **MasterClock** = a single monotonic timeline based on `QueryPerformanceCounter`, started when a
  show enters perform mode. It exposes `CurrentMediaTime`.
- Each `ISource` decodes ahead and selects, per present, the frame whose PTS is closest to
  `MasterClock.CurrentMediaTime` (+ per-clip `TimeOffset`). Drift is corrected by frame selection
  (drop/repeat), NOT by seeking — seeking a live decoder was a native-crash trigger in the old app.
- **Mismatched refresh rates (e.g. 165Hz + 60Hz):** decode is decoupled from present. The render
  thread presents each swapchain at that monitor's own vsync; both pull the correct frame for the
  same MasterClock time, so a 60Hz and a 165Hz monitor stay content-synced even though they present at
  different rates. `DXGI_SWAP_EFFECT_FLIP_DISCARD` + per-swapchain `Present(1, ...)` (vsync) or
  `Present(0, ...)` with a frame-time budget — chosen per monitor.
- **HAP tight multi-stream sync:** HAP frames are tiny to upload (BCn compressed), so all HAP sources
  can upload + be selected against the same MasterClock tick, giving frame-accurate alignment.

### 2.4 How each mode maps onto the ONE pipeline

The pipeline is invariant: device + swapchains + a fullscreen-quad pass that samples a source texture
with a per-output **sampling rectangle** (UV sub-rect) and an output viewport. Modes differ ONLY in
how sources bind to outputs and what UV sub-rect each output uses:

| Mode | Sources | Output → source mapping |
|------|---------|-------------------------|
| Spanning (one video across all monitors) | 1 source | each monitor gets an **equal share per screen** — its cell of the grid derived from the monitors' physical layout (N screens → 1/N each, scaled independently to fill; not pixel-proportional — see ADR 0003 Amendment 1) |
| Different video per monitor | N sources | output i samples source i, full UV |
| 4K split into 4×1080p | 1 source | each output samples its quadrant UV sub-rect (the old `VideoWallConfiguration` grid mapping) |
| HAP | HAP source(s) | identical binding; only the decode path + YCoCg shader variant differ |

No mode rebuilds the pipeline. Switching modes = re-binding source→output and updating UV sub-rects.

---

## 3. Solution structure (file-level)

```
MultiMon.sln
│
├─ MultiMon.Core/                         (no UI, no Win32 windows — pure logic + models)
│   ├─ Models/
│   │   ├─ MonitorInfo.cs                 [SALVAGE — copy from old, framework-agnostic]
│   │   ├─ VideoAssignment.cs             [SALVAGE — drop LibVLC CropGeometry string]
│   │   ├─ VideoWallConfiguration.cs      [SALVAGE]
│   │   ├─ ProjectConfiguration.cs        [SALVAGE — re-version schema]
│   │   ├─ AudioTrack.cs / AudioOutputDevice.cs  [SALVAGE shape, re-target engine]
│   │   ├─ ShowDefinition.cs              [NEW — runtime binding of sources→outputs+mode]
│   │   └─ GpuVendor.cs                   [SALVAGE]
│   ├─ Timing/
│   │   └─ MasterClock.cs                 [NEW — QPC timeline]
│   ├─ Sync/
│   │   └─ FrameSelector.cs               [NEW — pick frame for a given media time]
│   ├─ Show/
│   │   └─ ShowPlanner.cs                 [NEW — pure mapping: show+monitors → source/UV/clock per output]
│   └─ Abstractions/
│       ├─ ISource.cs                     [NEW — decode contract, publishes GPU texture]
│       ├─ IGraphicsDeviceProvider.cs     [NEW]
│       └─ IAudioEngine.cs                [NEW]
│
├─ MultiMon.Graphics/                      (Vortice D3D11 — the STABILITY-CRITICAL core)
│   ├─ GraphicsDeviceProvider.cs          [NEW — ONE device, ref-counted; multi-adapter selection]
│   ├─ AdapterSelector.cs                 [NEW — which IDXGIAdapter drives which output (HMONITOR→adapter)]
│   ├─ OutputWindow.cs                    [NEW — bare HWND + IDXGISwapChain1, persistent]
│   ├─ SwapChainPresenter.cs              [NEW — per-monitor present, vsync per refresh rate]
│   ├─ RenderLoop.cs                      [NEW — the single render/present thread]
│   ├─ FullscreenQuadPass.cs              [NEW — vertex+pixel pass, UV sub-rect + viewport]
│   ├─ TripleBuffer.cs                    [NEW — lock-free latest-frame handoff decode→render]
│   ├─ DeviceRemovedHandler.cs            [NEW — DXGI_ERROR_DEVICE_REMOVED: recreate + resume]
│   └─ Shaders/
│       ├─ Quad.hlsl                      [NEW — passthrough sample]
│       └─ HapYCoCg.hlsl                  [NEW — HapQ YCoCg→RGB; math from old VeldridHapRenderer]
│
├─ MultiMon.Hap/                            (net9.0 — pure HAP parsing/decoding, no GPU, no OS)
│   ├─ MovHapDemuxer.cs                   [NEW — MOV atom parse: sample table, fourcc, format byte]
│   ├─ HapFrameDecoder.cs                 [NEW — section headers + chunk reassembly → BCn payload]
│   ├─ HapTextureFormat.cs                [SALVAGE enum — Dxt1/Dxt5/YCoCgDxt5/Bc7/RgTc1]
│   ├─ HapLimits.cs                       [NEW — allocation ceilings for untrusted input]
│   └─ Snappy/SnappyDecoder.cs            [NEW — HAP chunk decompression]
│
├─ MultiMon.Decode/                         (Vortice.MediaFoundation + the HAP upload)
│   ├─ MediaFoundation/
│   │   ├─ MediaFoundationSource.cs       [NEW — IMFSourceReader → D3D11 texture via IMFDXGIDeviceManager]
│   │   └─ MfDeviceManager.cs             [NEW — bind MF to our ID3D11Device; HW decode + SW fallback]
│   └─ Hap/
│       └─ HapSource.cs                   [NEW — MultiMon.Hap frames → BCn D3D11 texture]
│
├─ MultiMon.Audio/
│   ├─ WasapiOutput.cs                    [NEW — shared-mode WASAPI per device, no NAudio]
│   ├─ AudioEngine.cs                     [NEW — routes AudioTrack→device, clocked to MasterClock]
│   └─ AudioRing.cs                       [NEW — lock-free ring buffer]
│
├─ MultiMon.Platform/                       (framework-agnostic OS services)
│   ├─ MonitorService.cs                  [SALVAGE — EnumDisplayMonitors/DEVMODE, as-is]
│   ├─ GpuCompatibilityService.cs         [SALVAGE — WMI detect; repurpose blacklist for HAP-capability only]
│   └─ HMonitorToAdapter.cs              [NEW — DXGI EnumOutputs → match HMONITOR to IDXGIAdapter]
│
├─ MultiMon.Control/                        (WPF — thin, video-free control panel)
│   ├─ App.xaml / App.xaml.cs             [NEW — DI bootstrap, GPU detect, device init]
│   ├─ MainWindow.xaml(.cs)               [NEW — assignment grid, perform button]
│   ├─ ViewModels/MainViewModel.cs        [NEW — issues controller commands only]
│   ├─ ProjectService.cs                  [NEW — .mmproj JSON save/load via ProjectConfiguration]
│   └─ PerformanceController.cs           [NEW — orchestrator (could live in Core; here for DI simplicity)]
│
├─ MultiMon.Stress/                         (headless harness — CARRY FORWARD the approach)
│   └─ StressHarness.cs                   [NEW — port the StressHarness.cs pattern to the D3D11 pipeline]
│
├─ MultiMon.Core.Tests/                     (net9.0 xUnit — everything that needs no Windows host)
│   ├─ HapParserTests.cs / HapHardeningTests.cs  [NEW — parse known + hostile HAP input]
│   ├─ ShowPlannerTests.cs                [NEW — pins the per-mode source/UV/clock mapping]
│   ├─ UvLayoutTests.cs / ProjectServiceTests.cs / CoreModelsSmokeTests.cs
│   ├─ FrameSelectorTests.cs              [NEW]
│   └─ MasterClockTests.cs                [NEW]
│
└─ MultiMon.Tests/                          (net9.0-windows xUnit — needs a real Windows host)
    ├─ AudioRingTests.cs / AudioAudibilityTests.cs  [NEW — WASAPI-side]
    └─ GpuCapabilityServiceTests.cs       [NEW]
```

Key namespaces: `MultiMon.Core.*`, `MultiMon.Hap{,.Snappy}`, `MultiMon.Graphics`,
`MultiMon.Decode.{MediaFoundation,Hap}`, `MultiMon.Audio`, `MultiMon.Platform`, `MultiMon.Control`.

---

## 4. Success criteria as checkpoints (Plan → Execute → Verify → Adjust)

Each checkpoint is verified by BOTH the headless stress harness AND a manual run. The harness is the
primary gate because it is repeatable and was what finally located the old deadlock.

### Checkpoint A — 1 video, 1 monitor, persistent pipeline, 50 open/close cycles
- **Build:** device + 1 swapchain + 1 MediaFoundationSource + render loop.
- **Definition of "cycle":** EnterPerform (bind source, show window, start clock) → hold N seconds →
  ExitPerform (hide window, stop clock, unbind source) — WITHOUT destroying device/swapchain.
- **Verify (harness):** `MultiMon.Stress --cycles=50 --windows=1`. PASS = 50/50 cycles reach steady
  presenting, zero wedge, clean exit; D3D11 live-object report (debug layer) shows NO growth in object
  count across cycles; private working set flat (±small) over 50 cycles.
- **Verify (manual):** run on **AMD, NVIDIA, Intel discrete, and an iGPU** machine — visually confirm
  playback + clean exit each.
- **Adjust trigger:** any object-count growth → ownership bug; any wedge → threading bug.

### Checkpoint B — N monitors synced, 50 cycles, no crash/wedge
- **Build:** swapchain per monitor, MasterClock, FrameSelector, multi-adapter selection, mismatched
  refresh-rate present.
- **Verify (harness):** `MultiMon.Stress --cycles=50 --windows=N --fullscreen` on the 3-GPU hybrid
  dev box (RTX 3080 + AMD iGPU + virtual monitor). PASS = 50/50, no wedge, content-sync drift within
  one frame between outputs (logged), clean exit.
- **Verify (manual):** spanning + per-monitor + 4K-split visually correct; 165Hz+60Hz pair stays in
  sync; pull a monitor's adapter mid-run (or force TDR via `dxcap`/driver restart) → app recovers
  (Checkpoint B+ for device-removed).
- **Adjust trigger:** any monitor wedges or desyncs > 1 frame → present/clock bug.

### Checkpoint C — all modes + audio + project I/O
- **Build:** HAP source + YCoCg shader, AudioEngine (WASAPI per device), ProjectService save/load.
- **Verify (harness):** `--cycles=50 --windows=N --hap --audio` mixed sources, 50/50 clean.
- **Verify (manual):** the FULL success-metric run — load a project (.mmproj), N monitors, mix of
  H.264 + HAP, audio routed per device, run 10+ perform cycles on each target GPU class, zero crash.
  Save/reload a project and confirm identical playback.
- **This is the ship gate.**

---

## 5. Build sequence (ordered milestones — each = one P→E→V→A cycle → checkpoint)

| # | Milestone | Produces | Model tier |
|---|-----------|----------|------------|
| 0 | Solution bootstrap: projects, DI, salvage `MonitorService`/`GpuCompatibilityService`/models, port `StressHarness` skeleton | compiling skeleton + harness stub | Sonnet |
| 1 | `GraphicsDeviceProvider` (one device, ref-counted) + `OutputWindow` (bare HWND + swapchain) + `RenderLoop` + `FullscreenQuadPass` rendering a test pattern, persistent across 50 show/hide cycles | persistent pipeline | **Fable** (render/present core) |
| 2 | `MediaFoundationSource` → D3D11 texture (HW decode + SW fallback) + `TripleBuffer` handoff; **Checkpoint A** | 1 video/1 monitor, 50 cycles | **Fable** (decode/handoff core) |
| 3 | `MasterClock` + `FrameSelector` + multi-monitor swapchains + `AdapterSelector`/`HMonitorToAdapter` + per-refresh-rate present; **Checkpoint B** | N synced monitors, 50 cycles | **Fable** (sync/present core) |
| 4 | `DeviceRemovedHandler` (TDR/device-removed recreate+resume) — verified by forced TDR | cross-vendor resilience | **Fable** (teardown/recreate core) |
| 5 | HAP: `HapContainerParser` + `HapSource` + BCn upload + `HapYCoCg.hlsl` (math from old renderer) | HAP playback | **Fable** for the GPU upload/shader; Sonnet for the parser |
| 6 | `AudioEngine` (WASAPI per device) clocked to MasterClock | audio routing | Sonnet (Fable spot-check the WASAPI callback threading) |
| 7 | Mode mapping in `PerformanceController` (spanning / per-monitor / 4K-split) + WPF control panel + `ProjectService` save/load | all modes + UI + I/O | Sonnet |
| 8 | **Checkpoint C** full success-metric run on all GPU classes; `/release` gate | ship candidate | Opus (sign-off) + Sonnet (gate execution) |

Any milestone that forces an architecture change (e.g. present model can't hold sync) escalates to
**Opus** for re-architecture before continuing.

---

## 6. Risks & mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|-----------|
| **TDR / `DXGI_ERROR_DEVICE_REMOVED`** (driver update, hang, vendor switch) | Med | Fatal | First-class `DeviceRemovedHandler` (Milestone 4): detect on Present/decode, recreate device+swapchains+resources+MF device manager, rebind sources, resume from MasterClock. Verified by forced TDR. |
| **Multi-adapter: monitor driven by a different GPU than the device** (3-GPU dev box) | High | Fatal/garbage output | `HMonitorToAdapter` maps each output's HMONITOR → its IDXGIAdapter via DXGI `EnumOutputs`. Default: create the device on the adapter driving the most/primary outputs; for outputs on another adapter, DXGI handles cross-adapter present for flip-model swapchains, OR (fallback) create a second device for that adapter. Decision logged per run. This exact issue contributed to old crashes. |
| **Mismatched refresh-rate sync (165Hz + 60Hz)** | High | Visible desync | Decode decoupled from present; both outputs select frame by MasterClock time, present at own vsync. Drift logged; corrected by frame select, never seek. |
| **MF hardware decode unsupported / fails on a GPU** | Med | No video | `MfDeviceManager` attempts HW, falls back to MF software decode automatically (same texture output path). Never crash — log + fallback (LESSON-PERF-001 generalized). |
| **HAP unsupported / BC7 not available on old iGPU** | Med | No HAP | HAP is an enhancement; reuse `GpuCompatibilityService` blacklist to gate HAP, fall back to MF decode of an H.264 proxy or skip. Never crash. |
| **WASAPI exclusive-mode contention / device removal** | Low | Audio glitch | Shared-mode WASAPI by default; handle `AUDCLNT_E_DEVICE_INVALIDATED` by re-enumerating + rebuilding the output (mirrors device-removed handling). |
| **Reintroducing UI-thread native teardown** (the original sin) | Med | Deadlock | Architectural guard: control project has no reference to Graphics/Decode device types; all native lifecycle is on render/decode threads. Code-review rule + harness gate. |
| **Triple-buffer / cross-thread texture race** | Med | Crash/tear | Lock-free publish of immutable per-frame textures; render thread only reads; debug-layer validation in harness. |

---

## 7. Salvage-as-reference vs build-fresh

**Salvage (copy, framework-agnostic, minimal edits):**
- `MonitorService.cs` — Win32 monitor enumeration, no WPF dependency beyond `System.Windows.Rect`
  (swap for a plain struct in Core). Use as-is.
- `GpuCompatibilityService.cs` — WMI GPU detect + vendor parse. Repurpose the blacklist to gate the
  HAP path's BC7/feature use, not LibVLC backends.
- Models: `MonitorInfo`, `VideoWallConfiguration` (grid mapping is exactly the 4K-split map),
  `ProjectConfiguration`, `AudioTrack`, `AudioOutputDevice`, `GpuVendor`. `VideoAssignment` salvaged
  but **drop** the LibVLC `CropGeometry` string (replace with a UV sub-rect).
- `StressHarness.cs` — carry forward the harness *approach* (headless cycle churn, wedge detection,
  results log, `--cycles/--windows/--fullscreen` args). Re-target from LibVLC to the D3D11 pipeline.
- `.mmproj`/`.mmplay` JSON schema — keep the format; bump a schema version field.

**Reference only (read for math/sequence, do NOT port):**
- `VeldridHapRenderer.cs` — the HapQ YCoCg→RGB conversion math and BCn-format mapping
  (`HapTextureFormat` → DXGI format) and the strict init order lesson. Rewrite in Vortice + HLSL.
- `CRASH_INVESTIGATION.md` / lessons — the deadlock + ownership rules baked into Section 1.1.

**Build fresh (everything native/threaded):** device provider, swapchains, render loop, MF decode,
HAP parser/source, audio engine, controller, control UI. None of the old WPF/LibVLC playback code
survives.

---

## 8. Open questions (need user decision before building)

- **Q1 — Control panel framework:** WPF (recommended; team knows it, thin video-free panel is trivial)
  vs WinUI 3 (more modern, heavier bootstrap). Default: **WPF** unless you want WinUI 3.
- **Q2 — HAP demux source:** the old app used FFmpeg (`HapFrameProvider`) to demux the .mov/HAP
  container. Keep an FFmpeg dependency just for HAP demux, or write a minimal MOV/AVI demuxer to stay
  zero-dependency? (HAP is usually in a QuickTime .mov.) Default: **minimal vendored MOV demuxer** to
  honor "ZERO third-party HAP dependency," but FFmpeg-for-demux-only is lower effort.
- **Q3 — Multi-adapter present strategy:** single device with DXGI cross-adapter present (simpler,
  works for flip-model) vs one device per adapter (more robust on truly separate GPUs). Default:
  **single device + cross-adapter present**, escalate to per-adapter devices only if Checkpoint B
  shows garbage/perf issues on the 3-GPU box.
- **Q4 — Audio backend:** raw WASAPI via Vortice/CsWin32 (zero extra dep, more code) vs a thin
  maintained lib. Default: **raw shared-mode WASAPI** (NAudio is explicitly out per old lessons).
- **Q5 — 4K-split source:** is the 4K→4×1080p split always one source sampled into quadrants
  (assumed here), or must it support pre-split files per quadrant? Default: **single source, UV
  quadrants** (matches old `VideoWallConfiguration`).
```

---

## Resolved Decisions — locked 2026-06-10

The §8 open questions are now decided. Build proceeds on these:

- **Q1 — Control panel UI: WPF.** Control-panel/config surface only; all video output is bare D3D11 swapchain windows (no `VideoView`, no airspace embedding). Most salvageable UI patterns; the embedding fragility that crashed the old app does not apply to a control-only panel.
- **Q2 — HAP demux: vendored minimal MOV atom parser.** Zero third-party dependency; we own 100% of the HAP path. (HAP frame decode is also from-scratch: BCn upload + HapQ YCoCg→RGB shader.)
- **Q3 — Multi-adapter: single D3D11 device + DXGI cross-adapter present** to start. Split to one-device-per-adapter ONLY if Checkpoint B reveals a multi-adapter sync problem — the test decides, not speculation.
- **Q4 — Audio: raw shared-mode WASAPI.** NAudio stays out. (The old WASAPI+LibVLC conflict lesson no longer applies — LibVLC is gone.)
- **Q5 — 4K-split: one 4K source → 4×1080p quadrants** (single decode; GPU samples four regions to four outputs, in sync by construction). Separate per-quadrant files are out of scope — that's the existing "different video per monitor" mode.

Bindings: **Vortice.Windows**. Runtime: **.NET 9 / C#**. Fable reserved for the render/decode/present/teardown core and hard bugs only.
