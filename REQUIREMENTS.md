# MultiMon System Requirements

## Operating system

- Windows 10 version 1809 (October 2018 Update) or later
- Windows 11 (all versions)

32-bit Windows is not supported. The distribution kit targets `win-x64`.

## .NET runtime

**Not required.** The distribution kit is self-contained — the .NET 9 runtime is bundled inside the
zip. No separate runtime installation is needed on the target machine.

## GPU and graphics

- **Direct3D 11 Feature Level 11.0** or higher (the minimum for the persistent D3D11 pipeline).
- Any GPU vendor is supported: AMD, NVIDIA, Intel discrete, Intel integrated (iGPU).
- A GPU that does not expose Feature Level 11.0 will fail at startup with a logged error.

### Hardware video decode

Media Foundation hardware decode (`IMFDXGIDeviceManager`) is attempted first. If the GPU or driver
does not support hardware decode for the requested codec, MultiMon falls back automatically to Media
Foundation software decode. Hardware decode is never required; the fallback path is exercised on
every startup and logged.

### HAP / BC7

The HapQ BC7 texture format is enabled only when the GPU reports D3D11 texture support for
`DXGI_FORMAT_BC7_UNORM`. On older integrated GPUs that do not support BC7, MultiMon falls back to
BC3. The fallback is automatic and logged at startup.

## Monitors

- At least one monitor is required to run the application.
- There is no hard upper limit on the number of monitors; the stress harness has been run with up to
  four simultaneous outputs.
- Monitors driven by different GPU adapters (cross-adapter) are supported via DXGI cross-adapter
  present.
- Mismatched refresh rates across monitors are supported; each output presents at its own vsync rate
  while remaining content-synced via the shared master clock.

## Supported video codecs

| Codec | Decode path |
|-------|-------------|
| H.264 (AVC) | Media Foundation (hardware or software) |
| H.265 / HEVC | Media Foundation (hardware or software) |
| HAP | From-scratch BCn upload + YCoCg shader (no third-party library) |
| HapQ | From-scratch BCn upload + YCoCg shader (no third-party library) |

Other codecs that Media Foundation supports on the system may also work but are not officially tested.

## Audio

- WASAPI shared-mode audio output.
- The default output device is used by default; per-monitor device selection is available in the
  control panel.
- Audio is optional; MultiMon runs without any audio device present.

## FFmpeg (optional — Convert to HAP only)

FFmpeg is **not bundled** and is **not required** for any playback feature.

It is required only if you want to use the **Convert to HAP** dialog to transcode source files.

To enable Convert to HAP:
1. Download FFmpeg from [https://ffmpeg.org/download.html](https://ffmpeg.org/download.html) or a
   trusted build site (e.g. gyan.dev, BtbN GitHub releases).
2. Add the folder containing `ffmpeg.exe` to your system `PATH`.
3. Verify: open a command prompt and run `ffmpeg -version`.

FFmpeg 5.0 or later is recommended. MultiMon has been tested with FFmpeg 8.0.

## Disk space

The self-contained distribution kit is approximately 100–150 MB unpacked (dominated by the .NET
runtime). No additional installation is performed; the kit runs from the folder where it is unzipped.

## Network

MultiMon does not require a network connection. All decode, render, and audio processing is local.
