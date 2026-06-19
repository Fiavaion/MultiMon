# ADR-0001 — Persistent D3D11 pipeline rebuild (Vortice.Windows)

**Status:** ACCEPTED (2026-06-10)
**Supersedes:** the entire WPF + LibVLCSharp implementation (archived `Old/MultiMon_LibVLC_V0087/`)
**Full detail:** [`REBUILD_ARCHITECTURE.md`](../../REBUILD_ARCHITECTURE.md) — this ADR is the index; that doc is the binding spec.

## Context
The old MultiMon embedded per-cycle LibVLC `MediaPlayer`s inside WPF `VideoView` controls. Two crash dumps
+ the headless `StressHarness` settled the root cause after 18 surface fixes failed: a **UI-thread teardown
deadlock** (the fullscreen vout thread needs the UI message loop to finish shutdown; tearing it down on the
UI thread self-deadlocks) compounded by **per-cycle native churn** leaking D3D9/DXVA2 state. The app could
not be patched — the failure is structural.

## Decision
1. **Persistent D3D11 pipeline.** ONE `ID3D11Device` + ONE `IDXGISwapChain1` per monitor, created once and
   reused for the whole session. Perform mode swaps content + shows/hides windows; it never creates or
   destroys the device/swapchains/shaders/state. (Kills both root causes.)
2. **Vortice.Windows** thin maintained bindings (D3D11/DXGI/Media Foundation/WASAPI). No hand-rolled COM,
   no GPU abstraction layer (Veldrid abandoned).
3. **HAP from scratch** — vendored MOV demux + BCn upload + HapQ YCoCg→RGB shader; zero third-party HAP dep.
4. **Media Foundation** decode → D3D11 texture (HW via `IMFDXGIDeviceManager`, automatic SW fallback).
   LibVLC dropped entirely.
5. **Bare D3D11 swapchain output windows**; the WPF control panel is separate and never embeds video.
6. **Audio: raw shared-mode WASAPI** (NAudio out).

## Consequences
- All native lifecycle moves off the UI thread; teardown happens only at app exit.
- Old NAudio/Veldrid/Snappier dependencies (and their debt — WASAPI conflict, NU1903 vuln) are eliminated.
- Stability is verified structurally by the stress harness + per-GPU manual runs at each milestone checkpoint.

## Alternatives rejected
Patch LibVLC (deadlock is structural) · Veldrid (hides device/swapchain lifetime we must own) · TerraFX raw
bindings (more hand-managed COM = more crash surface) · WPF `D3DImage` airspace interop (reintroduces the
render-thread coupling that crashed the old app) · keep LibVLC for H.264 only (its vout threading is the
thing being removed). See `REBUILD_ARCHITECTURE.md` §1.4.

## Open / proposed — pending measurement
- **Multi-adapter present strategy:** single device + DXGI cross-adapter present is the starting choice;
  splitting to one-device-per-adapter is **proposed — pending Checkpoint B** on the 3-GPU dev box. The test
  decides; do not pre-build the split.
