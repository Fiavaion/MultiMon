---
description: From-scratch HAP playback — vendored MOV demux + BCn upload + HapQ YCoCg→RGB shader (Vortice D3D11, NO Veldrid).
paths:
  - "MultiMon.Decode/Hap/**"
  - "MultiMon.Graphics/Shaders/**"
---

# HAP Playback Rules (rebuilt from scratch)

HAP is rebuilt with **zero third-party dependency**: vendored MOV atom parser + BCn compressed-texture
upload + a HapQ YCoCg→RGB pixel shader. **Veldrid is abandoned — do NOT port it.** The old
`VeldridHapRenderer.cs` (in `Old/`) is **reference-only**, for the YCoCg→RGB math and the
`HapTextureFormat → DXGI format` mapping.

## Pipeline (REBUILD_ARCHITECTURE.md §3, §2.4)
`HapSource` → vendored MOV demux → `HapContainerParser` (section headers, frame offsets, format byte) →
`HapFrame` (BCn payload + format enum) → BCn D3D11 texture upload → `HapYCoCg.hlsl` (HapQ variant) →
the same FullscreenQuadPass as every other source. HAP differs from H.264 ONLY in the decode path + the
YCoCg shader variant; the render pipeline is invariant.

## Critical invariants
1. **HAP is an enhancement, never a requirement.** If any part of the HAP path fails to init, log it and
   fall back to MF decode (H.264 proxy) or skip — NEVER crash. The success metric is crash-free playback.
2. **Gate on GPU capability BEFORE building the HAP path.** Reuse the salvaged `GpuCompatibilityService`
   blacklist to gate BC7/feature use. If unsupported, fall back immediately — do not attempt the upload.
3. **BCn formats map explicitly:** `Dxt1→BC1`, `Dxt5/YCoCgDxt5→BC3`, `Bc7→BC7`, `RgTc1→BC4`. Get the DXGI
   format + the per-format chroma path right (reference the old renderer's mapping, rewrite in Vortice).
4. **YCoCg→RGB happens in the pixel shader** (`HapYCoCg.hlsl`), not on the CPU. HapQ uses scaled-and-offset
   YCoCg; port the exact constants from the old shader math.
5. **HAP chunk decompression:** if a HAP frame is Snappy-compressed, decompress on the decode thread before
   upload. Keep it vendored/minimal — no heavyweight dependency.

## Tight multi-stream sync
HAP frames are tiny to upload (BCn compressed), so all HAP sources can upload + be selected against the
same `MasterClock` tick — frame-accurate alignment by construction. Sync via frame selection, never seek.

## Verification
Unit-test the parser against known HAP fixtures (`HapContainerParserTests`). The real gate is
`MultiMon.Stress --hap`: 50/50 clean cycles + a manual visual run on each target GPU class.
