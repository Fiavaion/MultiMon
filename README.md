# MultiMon

A multi-monitor video-wall performance tool for Windows and macOS — load video and audio onto monitor
slots, set up routing, then enter a fullscreen perform mode that spans or subdivides a single canvas
across all selected monitors.

[![Licence: EUPL-1.2](https://img.shields.io/badge/licence-EUPL--1.2-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6.svg)](REQUIREMENTS.md)
[![Platform: macOS](https://img.shields.io/badge/platform-macOS%2014%2B%20(Apple%20Silicon)-000000.svg)](docs/MAC_SETUP.md)
[![Latest release](https://img.shields.io/github/v/release/Fiavaion/MultiMon?include_prereleases)](https://github.com/Fiavaion/MultiMon/releases)

> **Status: `1.0.0`** — released 2026-10-18 for Windows (x64) and macOS (Apple Silicon). Stable on AMD and NVIDIA;
> the real-Intel (QuickSync) acceptance pass and a multi-vendor 4+ screen rig remain to be run on hardware — see CHANGELOG known limitations.

On Windows, MultiMon is built on a persistent Direct3D 11 pipeline (Vortice.Windows), Media Foundation
hardware decode with automatic software fallback, and from-scratch HAP support. The D3D11 device and
swapchains are created once at application start and reused for the entire session — no per-cycle teardown.

On macOS (Apple Silicon, macOS 14+), the same portable core (`MultiMon.Core`, `MultiMon.Hap`) drives a Metal
render core, HAP and VideoToolbox decode, CoreAudio output and an Avalonia control panel, with display
refresh matching and a performance readout. The platform choices are recorded in
[ADR 0005](docs/adr/0005-macos-platform-layers.md).

## Features

- **Three playback modes**
  - **Span** — one video source sampled equally across all selected monitors
  - **Individual** — one independent video source per monitor
  - **Split** — one high-resolution source divided into a configurable grid of outputs (e.g. 4K → 2×2 1080p)
- **HAP / HapQ playback** — from-scratch BCn texture upload and YCoCg-to-RGB shader; no third-party HAP library
- **Hardware decode** — Windows: H.264 and HEVC via Media Foundation (DXGI device manager) with automatic
  software-decode fallback; macOS: VideoToolbox. No LibVLC
- **Audio per output device, clocked to the master timeline** — WASAPI shared mode on Windows, CoreAudio on macOS
- **Project I/O** — save and load monitor assignments and routing as `.mmproj` files
- **Convert to HAP** — optional dialog to transcode a source to HAP via FFmpeg (see below)
- **Identify screens** — numbered overlays on every monitor to confirm assignment before performing
- **AMD / NVIDIA / Intel / integrated GPU support on Windows** — any Direct3D 11 Feature Level 11.0 GPU

## Screenshots

_Screenshots will be added before the 1.0.0 GA release._

## System requirements

See [REQUIREMENTS.md](REQUIREMENTS.md) for the full Windows list. In short:

- **Windows:** Windows 10 version 1809 or later, or Windows 11 (64-bit); any GPU with Direct3D 11 Feature
  Level 11.0 or higher
- **macOS:** macOS 14 or later on Apple Silicon. Intel Macs are deferred to 1.1.
- No .NET runtime to install on either platform — it's bundled in the distribution kit

## Install and run

### Windows

1. Download `MultiMon-<version>-win-x64.zip` from the [Releases](https://github.com/Fiavaion/MultiMon/releases)
   page and unzip it to any folder.
2. Run `MultiMon.Control.exe` (or the included `Launch MultiMon App.bat`).

### macOS

1. Download `MultiMon-<version>-macos-arm64.dmg` from the [Releases](https://github.com/Fiavaion/MultiMon/releases)
   page and drag MultiMon to Applications.
2. The app is signed with a Developer ID but not yet notarized: right-click → **Open** on first launch, or run
   `xattr -dr com.apple.quarantine /Applications/MultiMon.app`.

### Windows SmartScreen prompt

This release is unsigned, so Windows may show a SmartScreen warning on first run: click **More info** →
**Run anyway**. Code-signing is planned for a future release (see
[docs/adr/0004](docs/adr/0004-v1-launch-packaging-licence-signing.md)).

## FFmpeg — only for "Convert to HAP"

The **Convert to HAP** feature calls FFmpeg to transcode source files; FFmpeg is **not** bundled and is
**not** required for any playback feature. To enable it, install FFmpeg from
[ffmpeg.org](https://ffmpeg.org/download.html) (or a trusted build) and add the folder containing
`ffmpeg.exe` to your `PATH`. On macOS it also looks in the usual install locations (a user-local `bin`, then Homebrew).

## Build from source

One `master` branch holds both platforms. Each platform has its own solution, sharing the portable
projects (`MultiMon.Core`, `MultiMon.Hap`, `MultiMon.Core.Tests`); neither solution references the other
platform's projects. CI (`.github/workflows/ci.yml`) builds both on every push to `master` and every pull
request. How the two development machines share the branch: [docs/DEV_PATHS.md](docs/DEV_PATHS.md).

### Windows

Prerequisites: .NET 9 SDK, Windows 10/11, and Visual Studio 2022 or VS Code with the C# extension.

```powershell
git clone https://github.com/Fiavaion/MultiMon.git
cd MultiMon
dotnet build MultiMon.sln -c Release -warnaserror
dotnet test MultiMon.Core.Tests -c Release --no-build
dotnet test MultiMon.Tests -c Release --no-build
```

Run the stress harness (the primary verification gate):

```powershell
dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1
```

Produce a self-contained release zip:

```powershell
pwsh scripts/publish-release.ps1
```

### macOS (Apple Silicon)

Prerequisites: .NET 9 SDK, Xcode 15+ with the command-line tools, and the macOS workload
(`sudo dotnet workload install macos`). Full setup: [docs/MAC_SETUP.md](docs/MAC_SETUP.md).

```bash
dotnet build MultiMon.Mac.sln -c Release -warnaserror
dotnet test MultiMon.Core.Tests -c Release --no-build
dotnet run --project MultiMon.Stress.Mac -- --cycles=50 --windows=1
```

Produce the DMG (signed and notarized when the variables in [docs/RELEASE.md](docs/RELEASE.md) are set):

```bash
bash scripts/publish-release-mac.sh
```

Releases for both platforms are cut from a tag; see [docs/RELEASE.md](docs/RELEASE.md).

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
