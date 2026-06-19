---
description: Stability-critical D3D11 core — device/swapchain/render-loop ownership and lifetime. The Fable territory.
paths:
  - "MultiMon.Graphics/**"
---

# Graphics Core Rules (the stability-critical D3D11 layer)

This is the layer that killed the old app. Treat every change here as touching shared native
resource lifetime. Delegate non-trivial work here to the **`stability-core`** agent (Fable).

## Single-owner device (MANDATORY — LESSON-ARCH-001)
- There is **ONE** `ID3D11Device` for the whole process, owned by `GraphicsDeviceProvider`, ref-counted
  (`Acquire()`/`Release()`). NEVER create a second device except deliberately for a second adapter
  (multi-adapter fallback, logged per run).
- NEVER `new` a device, context, or swapchain outside the provider/`OutputWindow`. All native graphics
  objects have exactly one owner and a documented disposal point.

## Persistent pipeline — build once, reuse forever (LESSON-ARCH-002)
- Device + per-monitor `IDXGISwapChain1` + shaders + pipeline state are created **once at app start**.
- Entering/leaving perform mode **swaps content and shows/hides windows** — it does NOT create or destroy
  the device, swapchains, shaders, or state objects. NO per-cycle rebuild, NO periodic reset. (Per-cycle
  native churn + per-cycle teardown were the two root causes of the old crashes.)

## Teardown is off the UI thread, at app exit only (the V0087 deadlock rule)
- **No synchronous native GPU/decode/teardown call on the UI thread, EVER.**
- The only teardown is at app exit: stop decode loops → join render/decode/audio threads → dispose
  sources → dispose device. All off the UI thread.
- The render loop owns all `ID3D11DeviceContext` rendering + `Present`. Other threads NEVER touch the
  immediate context.

## Disposal order (explicit + ordered — LESSON-BUG-003 generalized)
Stop the producer before releasing the resource it feeds: stop decode/present loop → release views/textures
→ release swapchain → release device. Never dispose a swapchain while the render loop may still present to it.

## Device-removed is a first-class path (Milestone 4)
- Handle `DXGI_ERROR_DEVICE_REMOVED`/`DEVICE_RESET` on `Present`/decode: recreate device + swapchains +
  resources + MF device manager, rebind sources, resume from `MasterClock`. NEVER swallow it silently and
  NEVER crash. Verify by forced TDR.

## "Wedge on Nth cycle" pattern (LESSON-BUG-001)
A wedge/crash after N good cycles = accumulated native state / ownership corruption, NOT timing. Check:
1. More than one owner of a native resource?
2. Disposal order correct, and on the right thread?
3. Any object created per-cycle that should be persistent? (Run the D3D11 debug layer and diff the
   live-object count across cycles — growth = the bug.)
Do NOT add `Thread.Sleep`/`GC.Collect` to mask it.

## Verification gate
Any change here is verified by `MultiMon.Stress` (`--cycles=50`): zero wedge, **no D3D11 live-object growth
across cycles**, flat working set — plus a manual run on the relevant GPU class. Green unit tests ≠ done.
