# MultiMon

A Windows multi-monitor video-wall performance tool — load video and audio onto monitor slots, set up
routing, then enter a fullscreen perform mode that spans or subdivides a single canvas across all
selected monitors.

[![Licence: EUPL-1.2](https://img.shields.io/badge/licence-EUPL--1.2-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6.svg)](REQUIREMENTS.md)
[![Latest release](https://img.shields.io/github/v/release/Fiavaion/MultiMon?include_prereleases)](https://github.com/Fiavaion/MultiMon/releases)

> **Status: `1.0.0-rc.1`** — stable on AMD and NVIDIA; the real-Intel (QuickSync) hardware acceptance
> run is pending before the `v1.0.0` GA tag.

Built on a persistent Direct3D 11 pipeline (Vortice.Windows), Media Foundation hardware decode with
automatic software fallback, and from-scratch HAP support. The D3D11 device and swapchains are created
once at application start and reused for the entire session — no per-cycle teardown.

## Features

- **Three playback modes**
  - **Span** — one video source sampled equally across all selected monitors
  - **Individual** — one independent video source per monitor
  - **Split** — one high-resolution source divided into a configurable grid of outputs (e.g. 4K → 2×2 1080p)
- **HAP / HapQ playback** — from-scratch BCn texture upload and YCoCg-to-RGB shader; no third-party HAP library
- **Media Foundation decode** — H.264 and HEVC via hardware decode (DXGI device manager) with automatic
  software-decode fallback; no LibVLC
- **WASAPI audio** — raw shared-mode audio per output device, clocked to the master timeline
- **Project I/O** — save and load monitor assignments and routing as `.mmproj` files
- **Convert to HAP** — optional dialog to transcode a source to HAP via FFmpeg (see below)
- **Identify screens** — numbered overlays on every monitor to confirm assignment before performing
- **AMD / NVIDIA / Intel / integrated GPU support** — any Direct3D 11 Feature Level 11.0 GPU

## Screenshots

_Screenshots will be added before the 1.0.0 GA release._

## System requirements

See [REQUIREMENTS.md](REQUIREMENTS.md) for the full list. In short:

- Windows 10 version 1809 or later, or Windows 11 (64-bit)
- Any GPU with Direct3D 11 Feature Level 11.0 or higher
- No .NET runtime to install — it's bundled in the distribution kit

## Install and run

1. Download `MultiMon-<version>-win-x64.zip` from the [Releases](https://github.com/Fiavaion/MultiMon/releases)
   page and unzip it to any folder.
2. Run `MultiMon.Control.exe` (or the included `Launch MultiMon App.bat`).

### Windows SmartScreen prompt

This release is unsigned, so Windows may show a SmartScreen warning on first run: click **More info** →
**Run anyway**. Code-signing is planned for a future release (see
[docs/adr/0004](docs/adr/0004-v1-launch-packaging-licence-signing.md)).

## FFmpeg — only for "Convert to HAP"

The **Convert to HAP** feature calls FFmpeg to transcode source files; FFmpeg is **not** bundled and is
**not** required for any playback feature. To enable it, install FFmpeg from
[ffmpeg.org](https://ffmpeg.org/download.html) (or a trusted build) and add the folder containing
`ffmpeg.exe` to your `PATH`.

## Build from source

Prerequisites: .NET 9 SDK, Windows 10/11, and Visual Studio 2022 or VS Code with the C# extension.

```powershell
git clone https://github.com/Fiavaion/MultiMon.git
cd MultiMon
dotnet build MultiMon.sln
```

Run the stress harness (the primary verification gate):

```powershell
dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1
```

Produce a self-contained release zip:

```powershell
pwsh scripts/publish-release.ps1
```

## Known limitations

Outside the local-desktop / single-performer threat model for 1.0.0, documented rather than patched:

1. **HAP conversion resolves FFmpeg from `PATH`.** If an `ffmpeg.exe` sits in a world-writable or
   temporary directory earlier on your `PATH`, it will be used. Install FFmpeg only from a trusted
   source and keep your `PATH` clean.
2. **Project file media paths are accepted as-is**, including UNC paths. Only open `.mmproj` files you
   created; don't open project files from untrusted sources.

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Found a bug? Use the in-app
**Report a bug** button or open an [issue](https://github.com/Fiavaion/MultiMon/issues/new?template=bug_report.yml).

## Licence

Licensed under the **European Union Public Licence v1.2 (EUPL-1.2)** — see [LICENSE](LICENSE).
Third-party attributions are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).
