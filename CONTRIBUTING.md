# Contributing to MultiMon

Thanks for your interest in MultiMon. It's a Windows multi-monitor video-wall tool whose single
hardest requirement is **stability** — it must not crash, leak, or wedge during a live performance.
That goal shapes how contributions are reviewed.

## Build prerequisites

- .NET 9 SDK
- Windows 10 version 1809 or later (the Direct3D 11 pipeline is Windows-only)
- Visual Studio 2022 or VS Code with the C# extension

```powershell
dotnet build MultiMon.sln
dotnet test MultiMon.Tests
dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1
```

## Branching

- Base your work on the repository's default branch.
- One focused fix or feature per branch; use descriptive names (`fix/mf-sample-pool-depth`,
  `feat/osd-overlay`). No `wip` branches.
- Open a pull request and fill in the template.

## The verification gate — green unit tests are not "done"

A pull request that touches runtime behaviour is not ready until:

1. `dotnet test MultiMon.Tests` passes.
2. `dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1` (plus the relevant `--mode` /
   `--fullscreen` / `--audio` flags for your change) completes with **zero wedge and no Direct3D 11
   live-object growth across cycles**.
3. The PR describes which harness flags you ran and what you observed.

The stress harness is the real gate — it's what located the original teardown deadlock. Tests can pass
while the app still wedges, so harness verification (ideally on the GPU class you're changing) is
required.

## Native-resource rules (non-negotiable)

These exist because the previous implementation deadlocked on UI-thread teardown:

- The D3D11 device and swapchains are created **once** and reused — never created or destroyed per
  perform cycle.
- **No synchronous native GPU / decode / teardown call on the UI thread, ever.**
- Disposal order: stop decode threads → release views/textures → swapchain → device.

Changes to `MultiMon.Graphics` (device/swapchain/render-loop) and `MultiMon.Decode` native paths get
extra scrutiny — describe the resource ownership and threading implications in your PR.

## Code style

- Match the surrounding code. Vortice.Windows thin bindings only — no hand-rolled COM, no GPU
  abstraction layer.
- No `TODO`, `FIXME`, `TEMPORARY`, or "refactor later" in submitted code.
- Delete old code rather than commenting it out — git history is the backup.
- No speculative features or new dependencies beyond what your change needs.

## Reporting bugs

Use the in-app **Report a bug** button or open an
[issue](https://github.com/Fiavaion/MultiMon/issues/new?template=bug_report.yml). Include your GPU,
monitor setup, steps, and a log from `%LOCALAPPDATA%\MultiMon\Logs\`.

For **security** issues, do not open a public issue — see [SECURITY.md](SECURITY.md).
