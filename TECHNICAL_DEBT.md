# Technical Debt Register

*Zero-tolerance policy: core/foundation work is NEVER deferred. This file should stay near-empty.*
*Any item here has a cost estimate. Debt is resolved only with evidence: fix + verified-working check.*

---

## Active Debt

| # | Item | Cost | Notes |
|---|------|------|-------|
| D-006 | **Windows build owed** after `AudioRing` moved `MultiMon.Audio` → `MultiMon.Core/Audio` (Mac TODO 6, `5ee0333`) | **~15 min on the Windows box** | Namespace kept as `MultiMon.Audio`; `MultiMon.Audio.csproj` and `MultiMon.Tests.csproj` already reference Core, so statically it compiles — unproven until `dotnet build MultiMon.sln; dotnet test MultiMon.Tests` runs on Windows. Blocks `mac-port` → `master`. Optional follow-up there: rename the namespace to `MultiMon.Core.Audio`. |
| D-007 | Mac audio paths with no gate on a one-device box: device selection by UID, device-invalidated rebuild, mixer gain/pan/solo | **~1 h + a USB/HDMI audio device** | Found by the TODO 6 verify pass (2026-09-09). `--audio-device=`/`--audio-gain=`/`--audio-pan=` harness flags land in the TODO 6 fix commit so the code has a caller; the rebuild and multi-device routing need real hardware on the TODO 9 matrix. |
| D-005 | WASAPI `AUDCLNT_E_DEVICE_INVALIDATED` mid-run re-enumerate+rebuild | **IMPLEMENTED M7 — awaiting manual device-pull confirmation** | M7 `WasapiOutput` now: (1) **selects** a target endpoint by id (`AudioTrack.OutputDeviceId`, default fallback), (2) **enumerates** active render endpoints with friendly names (`EnumerateDevices`, verified on the dev box → VS248 / Oculus / Speakers), and (3) on `AUDCLNT_E_DEVICE_INVALIDATED` **rebuilds** the client/render/device on the render thread (re-resolve target-or-default, re-activate, restart, realign content position), bounded 3-attempt retry then log+stop — mirroring `DeviceRemovedHandler`. The control panel exposes the device dropdown. **Verified:** enumeration end-to-end; the audio render path stays harness-green (HAP+audio 50-cycle, drift 17ms, 0 underruns). **2026-09-04 audit:** the padding query sat OUTSIDE the rebuild `try`, so device invalidation killed the audio thread before `TryRebuild` could run — the manual gate would have failed. Fixed on `bugfix` (whole loop iteration inside the try). **Pending:** a manual device-pull mid-show to confirm the live rebuild/fallback (inherently a manual test). Move to Resolved once that manual gate is confirmed. |

## Resolved Debt

| # | Item | Resolved | How |
|---|------|----------|-----|
| D-004 | HAP **BC7** capability-gating + MF fallback not implemented | 2026-06-17 | **Resolved (cross-GPU hardening).** `GraphicsDeviceProvider.SupportsTextureFormat` (CheckFormatSupport) + `GpuCapabilityService.SupportsHap` now GATE the HAP path in `PerformanceController.BuildSource`: if HAP is unsupported (blacklist or the clip's BCn format unsupported on this GPU) or the clip can't open, it falls back to Media Foundation; if MF can't open it either (a pure HAP-codec .mov), the output is SKIPPED (black, logged) — never crashes. `FullscreenQuadPass.CreateSourceTexture` adds a backstop format check. **Verified:** HAP plays cross-GPU (NVIDIA + real AMD iGPU, 20-cycle, zero growth); app launches/refactor clean. The BC7-specifically-unsupported branch can't be reproduced on the dev box (NVIDIA/AMD-iGPU/WARP all support BC7) — it's exercised via `MULTIMON_DISABLE_HAP=1` (also a user escape hatch). |
| D-001 | NAudio `WasapiOut` used for audio output (WASAPI + LibVLC conflict risk) | 2026-06-10 | **Eliminated by rebuild** — audio is raw shared-mode WASAPI, single backend, LibVLC gone. |
| D-002 | Fix #13 (2500ms cleanup delay) "PENDING" in `WindowManager.cs` | 2026-06-10 | **Eliminated by rebuild** — no per-cycle teardown exists; the delay-as-fix antipattern is gone (LESSON-BUG-001). |
| — | Dual LibVLC instances (VideoWindow + AudioTrackManager) | V0013–V0015 | LibVLCProvider singleton (old app) |
| — | Pre-Skylake Intel iGPU crash | V0080 | GPU blacklist (old app) |
| D-003 | `Snappier 1.1.6` high-severity vulnerability (GHSA-pggp-6c3x-2xmx) | 2026-06-10 | **Eliminated by rebuild** — Veldrid (the transitive source) is abandoned; HAP chunk decompression will be vendored/minimal. |

---

## Rules

1. **Never defer foundation/core work.** A wedge in the render/decode/present/teardown core is not deferrable.
2. **Debt is resolved only when:** the fix is committed AND the success metric (50+ cycles, all GPU classes) is verified working in the stress harness + a manual run.
3. **Green tests ≠ resolved.** Unit tests can pass while the feature is broken (per `notes/lessons.md`).
4. Items unresolved for 2+ weeks are reviewed and either fixed or explicitly re-accepted with updated cost.
