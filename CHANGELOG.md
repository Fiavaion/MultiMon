# Changelog

All notable changes to MultiMon are documented in this file.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

---

## [1.0.0-rc.1] - 2026-06-18

This is the first release candidate of the MultiMon ground-up rebuild. The old WPF + LibVLCSharp
implementation is replaced in its entirety by a persistent Direct3D 11 pipeline.

### Added

- **Persistent D3D11 pipeline** — one `ID3D11Device` and one `IDXGISwapChain1` per monitor, created
  once at application start and reused for the entire session. Entering and leaving perform mode shows
  and hides windows; it never recreates the device, swapchains, shaders, or pipeline state.
- **Media Foundation decode** — H.264 and HEVC hardware decode via `IMFDXGIDeviceManager` bound to the
  application's D3D11 device; automatic software-decode fallback when hardware decode is unavailable.
- **HAP / HapQ playback** — from-scratch vendored MOV atom parser, Snappy chunk decompression, BCn
  (BC1/BC3/BC7) compressed-texture upload, and a HapQ YCoCg-to-RGB pixel shader. No third-party HAP
  library.
- **Three perform modes** — Span (one source across all monitors, equal share per screen), Individual
  (one source per monitor), and Split (one source divided into a configurable rows-by-columns grid of
  outputs; 4-output default is 2x2; grid is configurable).
- **WASAPI audio** — raw shared-mode WASAPI per output device, clocked to the QPC-based master
  timeline. Audio track linked to a video slot only when the track is audible.
- **Master volume and mute** — master level is owned by `PerformanceController` and persists across
  engine rebuilds and between perform cycles.
- **Project I/O** — save and load monitor assignments, source bindings, mode, and audio routing as
  `.mmproj` JSON files (schema version 2).
- **Convert to HAP dialog** — transcodes a source file to HAP via an external FFmpeg process; surfaces
  probe and metadata faults; progress tracking.
- **Identify screens** — toggles a numbered Windows-11-style overlay on every monitor; auto-hidden on
  perform entry.
- **Device-removed recovery** — `DeviceRemovedHandler` detects `DXGI_ERROR_DEVICE_REMOVED` on present
  or decode, recreates the device and all resources, and resumes from the master clock. Verified via
  `--inject-device-loss`.
- **Multi-adapter support** — single D3D11 device with DXGI cross-adapter present; output windows on a
  secondary adapter are composited via DWM. Verified on an AMD+NVIDIA dual-adapter box.
- **Stress harness** (`MultiMon.Stress`) — headless EnterPerform/ExitPerform cycle runner with D3D11
  debug-layer live-object tracking, wedge detection, and flags `--cycles`, `--windows`, `--fullscreen`,
  `--mode`, `--hap`, `--audio`, `--inject-device-loss`, `--force-sw-decode`, `--pause-after`, and
  others.
- **File logging** — timestamped log lines written to `%LOCALAPPDATA%\MultiMon\Logs\` (10-file
  rotation); startup banner captures GPU, monitor inventory, and D3D11 feature level.
- **Remote diagnostics** — global crash handlers write a `[CRASH]` stack to the log; present-rate
  watchdog and `TogglePause` markers assist remote debugging.
- **BC7 capability gating** — HAP BC7 format is enabled only when the GPU reports
  `D3D11_FORMAT_SUPPORT_TEXTURE2D` for `DXGI_FORMAT_BC7_UNORM`; older iGPUs fall back to BC3.
- **"Signal" UI theme** — immersive dark title bar on both the control window and the HAP dialog.

### Fixed

- **Multi-output pause freeze (Individual mode)** — on a four-output rig, pausing in Individual mode
  froze two to four screens until Escape. Root cause: each `MediaFoundationSource` exhausted its
  hardware decoder's output-sample pool (depth 8) on pause, causing `MF_E_SAMPLEALLOCATOR_EMPTY` on
  resume and killing the decode thread. Fix: cap the MF frame-timeline depth to 3.
- **Teardown timeout on multi-output rig** — the same sample-pool exhaustion left decode threads
  blocked at shutdown, producing a "teardown did not complete within 10s" warning. Resolved by the
  same depth cap.
- **Span mode equal-per-screen division** — span UV layout now divides equally per screen regardless
  of pixel resolution differences between monitors, matching the intended equal-share-per-screen
  semantic.
- **Audio garble after re-perform** — fixed clock-reset and WASAPI re-baseline on re-entry to perform
  mode.
- **Master volume lost on engine rebuild** — master volume and mute were reset to defaults each time
  the audio engine was rebuilt (e.g. on re-perform). Moved master ownership to
  `PerformanceController`.
- **FFmpeg argument injection** — `FfmpegHapConverter` now passes every argument as a discrete element
  via `ProcessStartInfo.ArgumentList` instead of a hand-quoted concatenated string.
- **`MFStartup` ref-count leak** — `MfAudioSource` now balances `MFStartup` if its `Configure()` call
  throws.
- **Cross-thread `StateChanged`** — `MainViewModel.OnControllerStateChanged` marshals to the WPF
  dispatcher before updating bound properties.
- **`async void Convert_Click` unhandled exception** — wrapped in try/catch to prevent an escaped
  exception from crashing the application.
- **Hotkey registration failure silent** — Space and Escape perform hotkeys now notify the user if
  registration fails, matching the behaviour of F11.
- **Bounded teardown join** — `App.OnExit` joins the teardown thread with a 10-second timeout and
  calls `Environment.Exit(0)` if the timeout is reached, preventing a wedged teardown from freezing
  the process at shutdown.
- **Present-model wedge under raised system timer** — changed from `Present(1)` to a WAITABLE
  swapchain with `Present(0)` to eliminate render-loop stalls caused by the 1ms system timer raised
  by Media Foundation and WASAPI.
- **HAP/MOV/Snappy untrusted-file OOM** — `MovHapDemuxer`, `SnappyDecoder`, and `HapFrameDecoder` now
  reject implausible sizes from file-supplied fields before allocating, preventing a crafted file from
  causing an out-of-memory condition in the decode thread.
- **ConvertToHap dialog fault surfacing** — probe and metadata failures are now surfaced in the dialog
  instead of silently stalling; metadata parse uses `TryParse` per field to degrade gracefully.
- **Harness windowed-span false positive** — windowed test rects are now tiled non-overlapping so the
  span UV clustering produces correct per-output slices.

### Known Limitations

- Real-Intel (QuickSync) acceptance run not yet completed; WARP cannot emulate Intel QuickSync. The
  hardware-gated go/no-go gate (multi-vendor 4+ screen rig, real Intel, live audio device-pull) remains
  open. This RC build should not be tagged v1.0.0 until that gate passes.
- The build is unsigned. Windows SmartScreen will show a warning on first run; see README for the
  "More info -> Run anyway" step.
- FFmpeg PATH-walk: the Convert-to-HAP dialog resolves `ffmpeg.exe` from PATH without filtering
  world-writable directories. Install FFmpeg only from a trusted source.
- Project file media paths (including UNC) are accepted as-is by `ProjectService`. Only open project
  files you created.
- Per-link audio mixer overrides (track volume set within a perform session) are not persisted to the
  `.mmproj` file. This is a known feature gap, not a regression.
- Live audio device-pull mid-show: the WASAPI invalidation-rebuild code is implemented and
  harness-verified; a live device-pull confirmation on the multi-output rig is still pending.
