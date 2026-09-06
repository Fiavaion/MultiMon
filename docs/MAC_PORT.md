# macOS port — what is already portable, and what a Mac implementation has to write

Status: preparation only. No macOS code exists. This records the seams as they stand after the
Core/Hap split, so the Mac work is platform layers rather than untangling.

Source: the Mac dependency inventory in `docs/sessions/2026-09-04-full-audit.md` ("Mac port viability" —
viable, option (a), ~12–18 engineer-weeks; a Vulkan/MoltenVK single-API rewrite was rejected as it would
need a GPU abstraction layer, which ADR 0001 forbids).

## Portable today (net9.0, no Windows reference, builds and tests on macOS)

| Assembly | Lines | Contents |
|---|---|---|
| `MultiMon.Core` | 1,098 | Models, `MasterClock`, `FrameSelector`, `UvLayout`, `ShowPlanner`, `ProjectService`, the abstractions |
| `MultiMon.Hap` | 745 | MOV atom demuxer, Snappy inflater, HAP frame decoder, format enum, input limits |
| `MultiMon.Core.Tests` | 1,656 | 126 tests covering exactly the two above |

Everything else is `net9.0-windows`: `MultiMon.Graphics` (2,328), `MultiMon.Control` (2,211),
`MultiMon.Audio` (1,291), `MultiMon.Decode` (1,095), `MultiMon.Stress` (960), `MultiMon.Platform` (954),
`MultiMon.Tests` (362).

`MultiMon.Core.Tests` is the regression gate for the portable half: it must stay green on macOS with no
Windows host in sight. The HAP fixture tests take their clip from `MULTIMON_HAP_FIXTURE`, else the dev
box's Windows path; absent both they no-op and the synthetic parser tests still run.

## The seams a Mac implementation fills

- **`Core.Abstractions.ISource`** — decode lifecycle plus `CurrentPts`; the frame handoff type is
  deliberately not in Core. A `VideoToolboxSource` implements it. `MultiMon.Decode/Hap/HapSource.cs`
  (the D3D11 upload) is the only Windows half of the HAP path: its Metal twin uploads the same BCn bytes
  `MultiMon.Hap` already produces.
- **`Core.Abstractions.IAudioEngine`** / `MultiMon.Audio.AudioEngine` — `Start`/`Stop` over
  `AudioTrack` → device routing, clocked to `MasterClock`. `WasapiOutput` and `AudioRing` are the
  Windows-specific parts; the ring is portable in shape but lives in a Windows assembly.
- **`Core.Abstractions.IMonitorService`** — `GetMonitors()` → `MonitorInfo` with `MonitorRect` bounds in
  virtual-desktop pixels. An `NSScreen` implementation fills it. Note `MonitorInfo.DeviceId` is a Win32
  device name (`\\.\DISPLAY1`) used as the HMONITOR→adapter match key and as the show-mapping key; on
  macOS it becomes an `NSScreen` display id, and only its use as an opaque key is portable.
- **The controller glue** — `MultiMon.Control/PerformanceController.cs`. Its mapping is now
  `Core.Show.ShowPlanner` and needs no port. What remains is native: D3D11 passes, MF/HAP source
  construction, WASAPI, output windows, device-removed recovery, and the worker-thread command queue.
  A Mac controller reimplements that glue against the same plan.
- **The control panel** — `MultiMon.Control` is WPF and does not port. It talks to the pipeline only
  through `IPerformanceController` (ADR 0003 D4), so a replacement panel reuses the whole view-model
  contract.

## Recommended stack (from the audit)

- Graphics: **Metal + CAMetalLayer** via the official Microsoft.macOS bindings, or SharpMetal (preview).
- Decode: **VideoToolbox**, zero-copy into Metal textures via `CVMetalTextureCache`.
- Audio: **CoreAudio / AVAudioEngine**.
- Displays and windows: **NSScreen / NSWindow**.
- Control panel: **Avalonia** (Skia-on-Metal).

No GPU abstraction layer, and no interfaces over D3D11 or Metal objects — ADR 0001 stands. The two
backends are separate platform assemblies behind the Core abstractions above, not one API over both.

## HAP ports as-is

Metal supports **BC1/BC3/BC4/BC7 on all Macs, Apple Silicon and Intel alike**. `MultiMon.Hap` already
produces exactly those BCn payloads and knows nothing about a GPU, so the HAP path needs only a Metal
texture upload — the same shape as `HapSource`. The HapQ YCoCg→RGB conversion is a pixel shader to port
to MSL, not logic to redesign.

## Rules

- **The macOS target must be built on a Mac.** `net9.0-macos` needs the Apple SDK and Xcode toolchain;
  it cannot be produced from Windows. Do not add a macOS TFM to a project built in CI on Windows.
- Windows must keep building and passing throughout. The portable assemblies are shared, not forked.
- Anything moved into Core or Hap must stay free of platform types — that is what keeps the split honest.

## Order of work

1. No further Windows-side hoisting is needed. `MultiMon.Core` and `MultiMon.Hap` already contain no
   D3D11 type; `DecodedFrame` and `IGraphicsDeviceProvider` live in `MultiMon.Graphics`, which a Mac
   port replaces wholesale with a Metal twin (a Metal `DecodedFrame` carrying an `MTLTexture`). Putting
   an opaque texture handle in Core would be the GPU abstraction layer the ADR forbids, for no gain.
2. On a Mac: `MultiMon.Platform.Mac` (`NSScreen` monitor service) — smallest seam, proves the toolchain.
3. `MultiMon.Graphics.Mac`: Metal device + `CAMetalLayer` per screen, persistent for the session, plus
   the fullscreen-quad pass and the UV sub-rect constant buffer.
4. `MultiMon.Decode.Mac`: HAP first (pure upload of what `MultiMon.Hap` already decodes), then
   VideoToolbox.
5. `MultiMon.Audio.Mac`: CoreAudio output against the same `MasterClock`.
6. The Mac controller glue, then an Avalonia panel over the existing view-model contract.

## Harness expectations

A Mac port is not done because it renders. It is done when a Mac stress harness gates the same criterion
as the Windows one: **50 open/close cycles, zero wedge, no sustained resource growth, flat working set.**

- Live-object counting is the D3D11 debug layer on Windows. The Metal equivalent is the allocation and
  resource counting under Metal's validation layer / `MTLCaptureManager` (`METAL_DEVICE_WRAPPER_TYPE=1`,
  `MTL_DEBUG_LAYER=1`); the harness must read a per-cycle resource count from it and fail on sustained
  growth, exactly as `StressHarness` does today.
- The same modes must be gated: span, split, individual, HAP, and audio.
- Cycle counts, wedge detection and the warm-up window carry over unchanged; only the counter is new.

## App icon (ready, not yet bundled)
The macOS icon is pre-built at `Ident/icons/MultiMon.icns` (PNG-in-ICNS, all sizes 16→1024 incl. @2x) with the matching
`Ident/icons/MultiMon.iconset/` folder. When the app bundle exists, copy the `.icns` to `Contents/Resources/MultiMon.icns`
and set `CFBundleIconFile` = `MultiMon` in `Info.plist`. Regenerate from the idents with `python Ident/icons/_build_icons.py`.
