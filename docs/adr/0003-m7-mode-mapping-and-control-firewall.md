# ADR 0003 — M7: mode mapping (UV sub-rects), multi-source binding, and the control firewall

**Status:** ACCEPTED (2026-06-16) · **Milestone:** M7 (builds toward Checkpoint C)
**Context:** [REBUILD_ARCHITECTURE.md](../../REBUILD_ARCHITECTURE.md) §2.4 (modes on the invariant pipeline), §3 (file list), §5 (M7 row); extends [0001](0001-persistent-d3d11-rebuild.md) / [0002](0002-m3-sync-and-multi-adapter-present.md).

## Context

M1–M6 give a persistent D3D11 pipeline that, today, binds **exactly one** `MediaFoundationSource`/`HapSource`
through **one** `FullscreenQuadPass` (full-UV passthrough) to **all** outputs via `OutputWindow.SetContent(pass)`.
M7 must deliver three perform modes (REBUILD_ARCHITECTURE.md §2.4) without ever rebuilding the pipeline:

| Mode | Sources | Per-output binding |
|------|---------|--------------------|
| Spanning | 1 | each output gets an **equal share per screen** — its cell of the layout-derived grid (amended 2026-06-17; see Amendment 1) |
| QuadSplit | 1 | each output samples its **quadrant** UV sub-rect of the one (4K) source |
| PerMonitor | N | output *i* ← source *i*, **full** UV |

Two capabilities are missing: (a) a **per-output UV sub-rect** (spanning, quad-split), and (b) **N concurrent
sources** bound to N outputs (per-monitor). (b) is the first time the rebuilt app holds more than one decode
source live at once — the historical "second concurrent decoder wedged in Opening" failure class (LESSON-ARCH-002
context). It is **not** a pipeline rebuild: device + swapchains + shaders + render loop stay invariant; the
additional source/pass objects are created once per show and torn down only off the UI thread (show-change or app
exit). The 50-cycle zero-live-object-growth harness gate is the proof.

M7 also adds the thin WPF control panel and `.mmproj` project I/O, and is where audio debt **D-005** (WASAPI
`AUDCLNT_E_DEVICE_INVALIDATED` → re-enumerate + rebuild, plus device **selection**) is resolved.

## Decisions

### D1 — UV-math lives in Core as a pure function, not in the Graphics core
`MultiMon.Core/Sync/UvLayout.cs` derives every per-output `UvRect`; `FullscreenQuadPass` only *consumes* one
(4 floats → constant buffer). Rationale: the graphics-core rule forbids logic in the device layer that doesn't
need the device; UV derivation is pure arithmetic with edge cases (monitor offsets, mixed DPI/resolution, gaps in
the virtual rect, odd grids) that must be unit-tested **without** the slow GPU harness gate.

```csharp
// MultiMon.Core/Models/UvRect.cs
public readonly record struct UvRect(float U0, float V0, float U1, float V1)
{
    public static UvRect Full => new(0f, 0f, 1f, 1f);
}

// MultiMon.Core/Sync/UvLayout.cs — PURE, allocation-light, unit-tested.
public static class UvLayout
{
    // Union bounding rect of the participating monitors, in virtual-desktop pixels.
    static MonitorRect Union(IReadOnlyList<MonitorRect> bounds);
    // Spanning: this output's monitor rect mapped into the union, normalized to 0..1 source space.
    static UvRect Spanning(MonitorRect output, MonitorRect union);
    // QuadSplit: cell (row,col) of a rows×cols grid.
    static UvRect Quadrant(int row, int col, int rows, int cols);
    // PerMonitor: UvRect.Full (the variation is the source binding, not the UV).
}
```
- **Spanning** maps the union of the participating `MonitorInfo.Bounds` onto the full source; each output's UV =
  `((mon.Left-union.Left)/union.Width, (mon.Top-union.Top)/union.Height, …)`. Gaps between non-contiguous monitors
  map to source regions that simply aren't shown (documented, acceptable).
- **QuadSplit** uses `VideoWallConfiguration.Rows/Columns` + `GridToMonitorMapping`: cell `(r,c)` →
  `U0=c/cols, U1=(c+1)/cols, V0=r/rows, V1=(r+1)/rows`. Default 2×2.

> **Amendment 1 (2026-06-17) — Spanning is equal-per-screen, not per-pixel.** The original spanning math above
> (normalize each monitor's pixel rect into the virtual union) handed a higher-resolution screen a proportionally
> larger slice of the source — e.g. a 2560-wide + 3840-wide pair split the source **40/60**. On the M8 manual run
> this was rejected: with *N* screens, each screen should get **1/N** of the source regardless of resolution
> (each scaling its share independently to fill — accepted distortion). `UvLayout` now clusters the participating
> monitors (by physical overlap along each axis) into a layout grid and returns each output's **grid cell** via the
> existing `Quadrant` math. New signature: `Spanning(int outputIndex, IReadOnlyList<MonitorRect> bounds)`; `Union`
> is removed (no remaining caller). Correct for a row of *N*, a column of *N*, and uniform grids; mixed-size grids
> are inherently ambiguous and out of scope. Regression-locked by `UvLayoutTests.Spanning_MixedResolution_StillSplitsFiftyFifty`.

> **Amendment 2 (2026-06-18) — "QuadSplit" → "Split", configurable grid.** The mode is renamed
> `ShowMode.QuadSplit` → `ShowMode.Split` (the grid was never fixed at 2×2 — `UvLayout.Quadrant(row,col,rows,cols)`
> always took arbitrary dimensions). The control panel now offers, in Split mode: **Auto** (default) deriving a
> near-square grid from the screen count (4→2×2, 9→3×3, 16→4×4, 6→2×3 via `ceil(sqrt n)` cols), or a **manual
> rows×columns** override (1–16 each), persisted in `VideoWallConfiguration` with a new `Auto` flag; an auto
> project recomputes for the loading machine's screen count rather than restoring the saved size. Mode is stored
> by name in `.mmproj`, so a `ShowModeJsonConverter` maps the legacy `"QuadSplit"` string to `Split` on load
> (regression-locked by `ProjectServiceTests.Load_LegacyQuadSplitMode_MapsToSplit`); the harness keeps
> `--mode=quadsplit` as an alias for `--mode=split`. `FullscreenQuadPass` is unchanged — that name is the
> render-geometry primitive (a fullscreen quad), unrelated to the mode.

### D2 — one `FullscreenQuadPass` per **source**, shared across the outputs it feeds
`FullscreenQuadPass.BindSource` rejects rebinding (one source per pass, by design). Therefore:
- **Spanning / QuadSplit** (1 source): **one** pass, bound to every output with that output's own `UvRect`.
- **PerMonitor** (N sources): **N** passes (one per source), each bound to its single output with `UvRect.Full`.

`OutputWindow.SetContent(FullscreenQuadPass? content, UvRect uv)` carries the per-output UV (render-thread-owned
state, marshalled via `RenderLoop.Invoke` exactly as today). The UV is **data** written per draw
(`UpdateSubresource` into a cbuffer created once in `BuildDeviceResources` / recreated in the device-removed
path) — **no per-cycle resource creation.**

### D3 — `PerformanceController` is the orchestrator and lives in `MultiMon.Control`
It owns the device provider, render loop, master clock, MF device manager, output windows, live sources, passes,
audio engine, and device-removed handler — so it must reference Graphics **and** Decode **and** Audio. The project
dependency graph forbids Graphics: `MultiMon.Decode` references `MultiMon.Graphics` (for `FrameTimeline`/
`DecodedFrame`), so placing the orchestrator in Graphics — which would then need to reference Decode — is a
dependency **cycle**. Core is also out (UI/Win32/GPU-free). The only assembly that already references all three is
`MultiMon.Control`, which is exactly where REBUILD_ARCHITECTURE.md §3 lists it ("orchestrator … here for DI
simplicity"). So the orchestrator lives in `MultiMon.Control` and implements the Core interface
`IPerformanceController`; the firewall (D4) is the interface + code discipline — the UI types (MainWindow,
ViewModel) reference only `IPerformanceController` + Core, never the concrete controller's Graphics/Decode types.
(A dedicated `MultiMon.Engine` assembly would make the firewall a compile-time guarantee, but that ceremony was
rejected in D4; revisit only if UI code is found leaking a native type.) `ApplyShow(ShowDefinition)` builds the
sources + pass(es) for the mode and binds them; a mode/show switch unbinds outputs (marshalled, so the render
thread drops the old pass) then stops + disposes old sources/passes off-thread and builds + rebinds new — **never**
touching device/swapchains/render loop (LESSON-ARCH-002). Teardown is the established ordered, off-UI-thread
sequence (stop sources → stop audio → stop loop → dispose passes → dispose sources → dispose audio/MF → release
device). The binding logic mirrors the path the stress harness gates at 50 cycles per mode.

```csharp
// MultiMon.Core/Abstractions/IPerformanceController.cs — Core types ONLY.
public interface IPerformanceController : IDisposable
{
    void ApplyShow(ShowDefinition show);
    void EnterPerform();                                  // Show() + clock.Start()
    void ExitPerform();                                   // clock.Stop() + unbind + Hide() — posts & returns
    IReadOnlyList<AudioOutputDevice> GetAudioDevices();
    event Action<PerformState>? StateChanged;
}
```

### D4 — Control firewall: ViewModel/Window see only Core types + `IPerformanceController`
The original sin was UI-thread native teardown. M7 guards it structurally:
- `MultiMon.Control` references `MultiMon.Graphics` at the **composition root only** (`App.xaml.cs` constructs the
  one `PerformanceController` at startup and immediately upcasts to `IPerformanceController`). The MainWindow,
  ViewModel, and all UI code reference **only** `IPerformanceController` + Core models — **zero** `Vortice.*`, zero
  `ID3D11*`, zero `ISource`.
- All controller commands (`ApplyShow`/`EnterPerform`/`ExitPerform`/`Dispose`) **post and return**; the UI thread
  never `await`s/joins a native teardown. Teardown runs on the controller's worker / render thread.
- (Stricter alternative considered — a `ControllerFactory` so Control references no Graphics type at all —
  **rejected** as unnecessary: a single composition-root `new` is standard DI and the firewall that matters is the
  ViewModel/Window never touching a native type. Enforced by reviewer-mode + project-reference discipline, not a
  factory.)

### D5 — `.mmproj` schema v2 + audio device selection (resolves D-005)
- `ProjectConfiguration` (salvaged shape, re-versioned): `SchemaVersion = 2`, `ShowMode Mode`,
  `List<VideoAssignment>` (LibVLC `CropGeometry` **dropped** → UV is derived, not stored), optional
  `VideoWallConfiguration`, `List<AudioTrack>` with per-track `OutputDeviceId`. `ProjectService` (System.Text.Json):
  `Save`/`Load`; `SchemaVersion < 2` migrates forward; an unknown higher version is logged + refused (never crash).
- **D-005:** `AudioEngine.GetDevices()` enumerates endpoints (`IMMDeviceEnumerator.EnumAudioEndpoints`); a track
  routes to its `OutputDeviceId` (open by id, else default). On `AUDCLNT_E_DEVICE_INVALIDATED` the WASAPI render
  thread re-enumerates, re-opens the target (or falls back to default if it vanished), rebuilds the
  `IAudioClient`/`IAudioRenderClient` **on that same render thread**, resumes from the MasterClock — bounded retry
  (≤3, backoff) then log + stop. Mirrors `DeviceRemovedHandler`. Resolved only when the `--audio` harness path **and**
  a manual device-pull run confirm recovery with video unaffected and no wedge.

## Verification (the gate — M7 closes only when ALL green AND manual confirmed)

Pure logic (no GPU): `dotnet test MultiMon.Tests` — `UvLayout` (dev-box two-monitor spanning slice, 2×2 quadrants,
full UV) + `ProjectService` (v2 round-trip, v1→v2 migration) green.

Harness, per mode, on the AMD+NVIDIA dev box — each must report `RESULT: PASS`, `wedges=0`, `maxGrowth=0`, flat ws:
```
--cycles=50 --windows=2 --fullscreen --video="…\CosmicPower_15Sec.mp4"                         # spanning
--cycles=50 --windows=2 --fullscreen --mode=quadsplit --video="…\4k_10Sec_Quads.mp4"           # quad-split
--cycles=50 --windows=2 --fullscreen --mode=permonitor --video=<A> --video2=<B>                # per-monitor (N sources)
--cycles=50 --windows=2 --fullscreen --hap --audio --video="…\Hap\5sec_hap.mov"                # compose (M5/M6 survive)
--cycles=50 --windows=2 --fullscreen --raise-timer                                             # present regression (unchanged)
```
Per-output `--capture` BMPs confirm: spanning = adjacent slices of one frame; quad-split = distinct quadrants;
per-monitor = each output its own clip.

Manual (the "done" half): launch `MultiMon.Control.exe`, assign clips, run each mode visually (spanning continuous
across both monitors; quad-split correct quadrants; per-monitor two synced clips); pull the selected audio device
mid-show (D-005 rebuild/fallback, video unaffected); save a `.mmproj`, reload, confirm identical playback.

## Adjust triggers
- Any live-object growth across cycles in any mode → ownership bug (esp. the per-monitor multi-source path).
- Any UI-thread block on teardown → firewall regression (code-review gate; the harness can't catch it).
- Spanning/quad-split UV visibly wrong → `UvLayout` bug (caught earlier by unit tests).

## Model tiers
M7 is **Sonnet-led** (Core pure logic, models, `ProjectService`, WPF panel). **Fable / `stability-core`
spot-check** only on: the `FullscreenQuadPass`/`OutputWindow` UV-binding diff (D2), the per-monitor multi-source
binding + teardown (D3), and the WASAPI render-thread rebuild (D5). This ADR is the Opus sign-off gate before code.
