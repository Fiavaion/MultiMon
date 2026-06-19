# ADR 0004 — 1.0.0 launch: distribution, licence, code-signing, version/tag scheme

**Status:** ACCEPTED (2026-06-18) · **Milestone:** Launch (gates the v1.0.0 tag; follows Checkpoint C)
**Context:** [REBUILD_ARCHITECTURE.md](../../REBUILD_ARCHITECTURE.md) (Success Metric, ship gate); [CLAUDE.md](../../CLAUDE.md) (Change Boundaries, model policy); extends the M0–M8 milestone chain. Companion to the launch plan in [notes/CURRENT_STATUS.md](../../notes/CURRENT_STATUS.md).

## Context

M8 is code-complete on `rebuild` (`master == rebuild`): 93/93 tests green, stress harness `maxGrowth=0` on the AMD+NVIDIA dev box. The code is ready; what remains before a public `v1.0.0` tag is **launch packaging, docs, and the hardware ship gate** — not any architecture change.

A pre-tag staffing pass (planner / stability-core / reviewer / inventory) surfaced four launch decisions that must be made consciously before the tag, plus the open blockers they gate. There is currently **no version marker** in any `.csproj` (shipped exe would report the framework default, not 1.0.0), **no LICENSE / README / CHANGELOG / REQUIREMENTS / ATTRIBUTION** at the repo root, and **no reproducible publish config** — the working kit (`MultiMon-MultiGPU-Test`) was hand-built. The app uses native MF/WASAPI/D3D11 (Windows-only) and an **optional** FFmpeg dependency for the Convert-to-HAP path only.

## Decisions

### D1 — Distribution: self-contained single-file zip, FFmpeg as an external prerequisite
Ship `MultiMon-1.0.0-win-x64.zip` produced by `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`, containing the Control app + the Stress harness + launchers + the docs from D2. No .NET runtime install required on the target machine. **FFmpeg is NOT bundled** — it is documented as an external prerequisite needed *only* for the optional Convert-to-HAP dialog. Rationale: bundling FFmpeg would pull its LGPL/GPL terms into the distribution for a feature most performers never touch; keeping it external leaves MultiMon's own licence (D2) clean and the kit small. An installer (Inno/MSIX) is **deferred to 1.0.x**. The publish invocation must be committed as a reproducible release script (no more hand-built kits).

### D2 — Licence: EUPL-1.2 (European Union Public Licence)
MultiMon ships under **EUPL-1.2**. It is the EU's official open-source copyleft licence and is explicitly compatibility-listed; the permissive third-party deps we ship — Vortice.Windows (MIT), Microsoft.Windows.CsWin32 (MIT), Microsoft.Extensions.DependencyInjection / System.Management (MIT) — combine with it without conflict (xUnit/Apache is test-only, not distributed). Deliverables: a root `LICENSE` (EUPL-1.2 text) **and** a `THIRD-PARTY-NOTICES` file enumerating every distributed dependency with its licence. The HapQ YCoCg→RGB shader math origin (referenced from the old HAP/Veldrid renderer) and the vendored Snappy decoder must be traced and attributed if derived from any reference code.

### D3 — Code-signing: ship 1.0.0 unsigned, document the SmartScreen step
The self-contained exe ships **unsigned** for 1.0.0. The README documents the SmartScreen "More info → Run anyway" step. Code-signing (OV cert) is logged as a **1.0.x fast-follow** once distribution volume justifies the cert cost. Rationale: signing is a hardening enhancement, not a stability requirement — the Success Metric does not depend on it. This is a tag-blocker only as a *decision* (now made), not as work; choosing unsigned unblocks the tag.

### D4 — Version marker + tag scheme: `Directory.Build.props` = single source, RC-first to GA
- Create `Directory.Build.props` at the repo root carrying `Version`, `AssemblyVersion`, `FileVersion`, `Company`, `Product`, `Copyright` so **every** assembly reports the version from one place — replacing the informal "milestone number is the version" convention.
- **RC-first:** build `1.0.0-rc.1`, run **Checkpoint C** (the ship gate) on the real multi-vendor + Intel rig against the RC. Promote to `1.0.0` only on a clean pass. Rationale: rig time is one-shot; the RC gives a buffer if the rig surfaces a late issue.
- **Branch→tag→release:** finish packaging/docs on `rebuild`; on a clean Checkpoint C pass, **merge `rebuild`→`master`** (fast-forward — currently equal), then create the **annotated `v1.0.0` tag on `master`**, `git push --tags`, and publish the GitHub release with the zip attached. Never tag the feature branch.

## Out of threat model — documented, not fixed (reviewer pass)
Both parked items are outside the local-desktop / single-performer / non-networked threat model and ship **documented as known limitations** in the README, not patched for 1.0.0:
- **FFmpeg PATH-walk** ([FfmpegHapConverter.cs:48-51](../../MultiMon.Platform/Conversion/FfmpegHapConverter.cs)) — bundled-first resolution already defangs the main self-compromise case.
- **Project-path UNC validation** ([ProjectService.cs:43-46](../../MultiMon.Core/ProjectService.cs)) — `.mmproj` media paths are taken as-is; only open project files you created. No code-execution vector.
(One-line fixes exist for each if a future release promotes them; not warranted now.)

## Go/No-Go gate (ALL must be true to tag v1.0.0)

**Stability (the acceptance bar — HARD):**
- Harness `--cycles=50` PASS on all modes (span/individual/hap/split), `maxGrowth=0`, flat working set, clean teardown — on the dev box AND the rig.
- Real **multi-vendor 4+ screen rig** manual perform, all modes, zero crash/wedge.
- **Real-Intel pass** — QuickSync confirmed alive via the GPU banner (NOT WARP) + the HAP-disabled fallback run + `--force-sw-decode` run. *(The one true open stability blocker — no flag substitutes; WARP can't emulate QuickSync.)*
- Device-removed ride-through: `--inject-device-loss` green + one real adapter disable→enable.

**Hardening / Docs / Build-Dist (HARD unless noted):**
- `/release` gate re-run PASS on the RC build (spawns its own security/UX/perf sub-agents — not duplicated by hand).
- Parked items documented per the section above.
- LICENSE (EUPL-1.2), THIRD-PARTY-NOTICES, README, CHANGELOG present; REQUIREMENTS (GPU/OS/.NET 9/FFmpeg-for-HAP) — strongly preferred (soft).
- `dotnet publish` per D1 runs clean and the kit launches on a machine with no .NET SDK.
- Published exe metadata reads 1.0.0 (D4).

**Soft (documentable if not done):** D-005 live audio device-pull on the rig (code verified, live-pull confirm-only); forced real `DEVICE_REMOVED` via scripted elevated adapter cycle.

## Harness gaps the gate must account for (stability-core)
- **No `--disable-hap` flag** — the Intel-iGPU HAP capability-gate fallback is `MULTIMON_DISABLE_HAP` env-only, not harness-driven. Add the flag or accept a manual env step in the Intel run.
- **No real HW→SW auto-fallback exercise** — `--force-sw-decode` bypasses detection rather than exercising it; the auto-transition is manual-only (read the log banner on real Intel) until a `--decode-probe` mode exists.
- **No D-005 audio-device-pull injection** — mid-show endpoint removal stays manual.
None of these block a gate that *accepts the corresponding manual confirmations on real hardware*; they only block a fully harness-automated gate.

## Model tiers
Launch packaging (D1 publish script, D2 docs/licence, D4 version marker) is **Sonnet-led** (`reviewer`). The hardware ship gate + any native render/decode/present/teardown triage is **Fable / `stability-core`** only (Opus fallback when Fable is unavailable, as this session). This ADR + the final go/no-go call is the **Opus main-session** sign-off — not delegated.
