# HAP Playback Development Log

This document tracks HAP playback bugs, attempted fixes, and results.

---

## Current Issue: HAP Fullscreen Regression

**Status:** INVESTIGATING
**Date:** 2025-01-17

### Symptoms
1. HAP video window not going completely fullscreen (black areas visible but not filling monitors)
2. No video playback occurring in perform mode
3. This is a REGRESSION from a previous state where fullscreen worked but playback didn't

### Previous State (before regression)
- Window DID go fullscreen properly
- Playback was NOT working (no video displayed)

### Current State (regression)
- Window NOT going fullscreen properly (partial black coverage)
- Playback NOT working
- Essentially reverted to an earlier broken state

### Screenshot Analysis (2025-01-17)
- Black areas visible where HAP window should be
- Window not spanning full monitor area
- No video content rendering

---

## Attempted Fixes

### Attempt #1: Force layout update before/after SetWindowPos
**Date:** 2025-01-17
**Hypothesis:** HwndHost (used for Veldrid rendering) may need WPF layout system to complete before window positioning works correctly. The WPF layout pass may not have completed when SetWindowPos is called.
**Changes Made:**
- Added `window.UpdateLayout()` after `window.Show()` and before getting HWND
- Added another `window.UpdateLayout()` after `SetWindowPos`
- File: `WindowManager.cs` lines 478-491
**Result:** FAILED - Fullscreen still corrupted
**Logs:** No change in behavior
**Next Steps:** Try Dispatcher.BeginInvoke to delay SetWindowPos until after WPF render pass

### Attempt #2: Use Dispatcher.BeginInvoke for delayed positioning
**Date:** 2025-01-17
**Hypothesis:** WPF needs a complete render cycle before HwndHost dimensions are correct. Using Dispatcher with Loaded priority will queue the positioning after layout/render completes.
**Changes Made:**
- Wrap SetWindowPos in Dispatcher.BeginInvoke with DispatcherPriority.Loaded
- File: `WindowManager.cs`
**Result:** FAILED - Fullscreen still corrupted
**Logs:** Window still not filling monitors completely
**Next Steps:** Try setting explicit Width/Height before Show()

### Attempt #3: Set explicit window dimensions before Show()
**Date:** 2025-01-17
**Hypothesis:** WPF window with HwndHost needs explicit Width/Height set before Show() to properly size the child HwndHost. Without explicit dimensions, the HwndHost may default to 0x0 or minimal size.
**Changes Made:**
- Set window.Width and window.Height to span dimensions before Show()
- File: `WindowManager.cs`
**Result:** FAILED - Fullscreen still corrupted
**Logs:** Window positioning doesn't matter - renderer init failing

### Attempt #4: Fix Veldrid swapchain depth format
**Date:** 2025-01-17
**Hypothesis:** Veldrid swapchain is being created with invalid depth format `D32_Float_S8_UInt`. Video rendering doesn't need depth buffer. E_INVALIDARG error confirms this.
**Changes Made:**
- Remove depth format from swapchain OR use None/null
- File: `VeldridHapRenderer.cs` line 109
**Result:** FAILED - Wrong fix, E_INVALIDARG persisted
**Logs:** `[HapRenderer] Initialization failed: HRESULT: [0x80070057], Module: [General], ApiCode: [E_INVALIDARG/Invalid arguments]`

### Attempt #5: Fix ACTUAL root cause - Invalid vertex semantic (THE FIX)
**Date:** 2025-01-17
**Hypothesis:** Line 260 uses wrong VertexElementSemantic - Position marked as TextureCoordinate instead of Position. D3D11 rejects invalid vertex layouts with E_INVALIDARG.
**Changes Made:**
- Changed line 260: `VertexElementSemantic.TextureCoordinate` → `VertexElementSemantic.Position`
- File: `VeldridHapRenderer.cs` line 260
**Result:** TESTING
**Logs:** TBD - expecting `[HapRenderer] Initialized: WxH, D3D11`

---

## Architecture Notes

### HAP Playback Components
1. `HapVideoWindow.xaml.cs` - WPF window that hosts the player
2. `HapVideoHost.cs` - HwndHost creating native window for Veldrid D3D11
3. `VeldridHapRenderer.cs` - GPU renderer with pre-compiled HLSL shaders
4. `HapPlayer.cs` - Precision timing and frame scheduling
5. `HapFrameProvider.cs` - FFmpeg-based frame extraction

### Key Initialization Flow
1. HapVideoWindow.Loaded -> Subscribes to VideoHost.HostReady
2. HapVideoHost.BuildWindowCore -> Creates native HWND
3. HapVideoHost.InitializeRenderer -> Creates VeldridHapRenderer
4. VeldridHapRenderer.Initialize -> Creates D3D11 device, swapchain, pipeline
5. HostReady event fires -> HapPlayer created
6. Video loaded -> OpenAsync -> Play()

### Known Issues to Check
- [ ] Window positioning/sizing during fullscreen
- [ ] Renderer initialization timing (deferred init if size is 0x0)
- [ ] Swapchain creation with correct dimensions
- [ ] Play() being called before renderer is ready

---

## Investigation Queue

1. Check what code changed between "fullscreen working" and current state
2. Verify HapVideoHost is receiving correct size on fullscreen
3. Check if renderer initialization is completing
4. Verify Play() is actually being called
5. Check console output for initialization messages

---

## Log Format Template

```
### Attempt #N: [Brief Description]
**Date:** YYYY-MM-DD
**Hypothesis:** What I think is wrong
**Changes Made:** Files and lines modified
**Result:** SUCCESS / PARTIAL / FAILED
**Logs:** Relevant console output
**Next Steps:** What to try if this fails
```
