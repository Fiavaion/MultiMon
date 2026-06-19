## What does this PR do?

<!-- One or two sentences. Link any issue it fixes: "Fixes #123". -->

## Verification

<!-- A PR that touches runtime behaviour needs a verification path. -->

- [ ] `dotnet test MultiMon.Tests` — all tests pass
- [ ] `dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1` (+ relevant flags) — zero wedge, no D3D11 live-object growth

Stress harness flags used:

```
dotnet run --project MultiMon.Stress -- <paste your flags here>
```

Observed result:

<!-- Harness output: any wedge, object-count growth, or working-set trend? Which GPU did you test on? -->

## Checklist

- [ ] No `TODO`, `FIXME`, or `TEMPORARY` comments in submitted code
- [ ] Old code deleted, not commented out
- [ ] No new speculative features or dependencies beyond this change's scope
- [ ] Native-resource rules followed (no per-cycle device/swapchain create/destroy; no UI-thread native calls)
