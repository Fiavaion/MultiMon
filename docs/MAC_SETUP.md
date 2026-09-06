# MultiMon — macOS setup and dev-phase runbook

**Audience:** the Claude Code instance opened on the Mac, first session. Read this file top to bottom before
touching anything. It is written to be followed literally; every step ends in a check you can run.

**What you are inheriting:** the Windows app at `master` = `2a00473` (2026-09-06): the 2026-09-04 audit build (`0c395c0`)
plus the final brand idents, Windows/macOS icon sets and the wired `ApplicationIcon`; harness-green, zero known bugs. The portable half (`MultiMon.Core`, `MultiMon.Hap`, `MultiMon.Core.Tests`)
already builds and tests on macOS with no Mac code written. Your job is to add the macOS platform layers
behind the existing Core abstractions, gated by a Mac stress harness, without touching the Windows build.

**Hierarchy of truth on the Mac (highest first):** `CLAUDE.md` (project) → this file → `docs/MAC_PORT.md` →
`REBUILD_ARCHITECTURE.md` + `docs/adr/` → everything else. Where `CLAUDE.md` names something Windows-specific
(a `taskkill`, a `.exe`, `dotnet build MultiMon.sln`), section 5 gives the Mac equivalent; the *rules* in
`CLAUDE.md` (threading model, no GPU abstraction layer, harness gate, no `Thread.Sleep` fixes, reviewer mode,
done-means-verified) bind unchanged.

---

## 0. What the zip must contain (verify after unzipping)

The project folder was zipped whole, so these travel with it. Confirm each is present:

| Path | Why it matters |
|---|---|
| `CLAUDE.md` | project rules — git-excluded on Windows, exists only on disk; must be in the zip |
| `.claude/agents/`, `.claude/rules/`, `.claude/commands/`, `.claude/settings.local.json` | project-level agents (`stability-core`, `planner`, `reviewer`), the graphics/decode/HAP rule files, slash commands, permission allowlist |
| `notes/` (`CURRENT_STATUS.md`, `TODOs.md`, `lessons.md`) | gitignored continuity files; the restart package |
| `docs/` (`MAC_PORT.md`, `adr/`, `sessions/2026-09-04-full-audit.md`) | the port plan, the founding ADRs, the audit record |
| `mac-handoff/` | copies of the **global** `~/.claude` routing files (agents, modelswitcher skill, global `CLAUDE.md`, routing keys from `settings.json`). These do NOT live in the project on Windows; they were copied in for the transfer. Section 3 installs them. If the folder is missing, Appendix A–C contain the same files verbatim. |
| `.git/` | full history (9.6 MB). `origin` = `https://github.com/Fiavaion/MultiMon.git`; `master` and `rebuild` are pushed and equal (`2a00473` + this runbook commit). |
| `Old/`, `OldProjectDoNotTouch/` | legacy LibVLC app, read-only reference (salvage logic, never code) |

Not in the zip and needed: **test media**. Copy `D:\testing Videos\` (at minimum `Hap\5sec_hap.mov`, one
H.264 mp4, one 4K mp4, one mp3) to the Mac. `MultiMon-test-media.zip` carries exactly that set and extracts to
`~/Desktop/AIprojects/MultiMonMedia/`; section 2 points `MULTIMON_HAP_FIXTURE` at it.

Delete after unzipping (Windows build output — harmless but large and confusing): every `bin/` and `obj/`
directory, and `.claude/worktrees/` if present.

```bash
cd ~/Desktop/AIprojects/MultiMon    # both zips extract into ~/Desktop/AIprojects/
find . -type d \( -name bin -o -name obj \) -not -path './Old/*' -prune -exec rm -rf {} +
rm -rf .claude/worktrees
git status --short | head           # expect: empty (mac-handoff/ and notes/ are ignored)
git log --oneline -1                # expect the newest master commit ("docs: macOS setup ..." or later);
                                    # app code + icons at 2a00473 (2026-09-06)
```

---

## 1. Mac toolchain

Do each step and its check. Do not skip a check because the previous one passed.

1. **Xcode + command-line tools** (Metal, VideoToolbox, AppKit headers; the macOS .NET workload needs them).
   ```bash
   xcode-select -p || xcode-select --install
   xcodebuild -version                 # Xcode 15+; if prompted: sudo xcodebuild -license accept
   ```
2. **.NET 9 SDK** — the native build (arm64 on Apple Silicon, x64 on Intel), not Rosetta.
   ```bash
   dotnet --version                    # expect 9.0.x (the Windows box used 9.0.306)
   dotnet --info | grep -E "RID|Architecture"
   ```
3. **macOS workload** — provides the `net9.0-macos` TFM and the `Microsoft.macOS` AppKit / Metal /
   VideoToolbox / CoreAudio bindings.
   ```bash
   sudo dotnet workload install macos
   dotnet workload list                # expect: macos
   ```
4. **Claude Code** signed in to the same account (`claude --version`); **git** (ships with the Xcode CLT).
5. **FFmpeg** — only for Convert-to-HAP, not needed for the port's first phase: `brew install ffmpeg`.

---

## 2. Prove the portable half before writing any Mac code

The first gate. It must be green with **zero** changes; it proves the toolchain, the zip and the Core/Hap
split at once.

```bash
export MULTIMON_HAP_FIXTURE=~/Desktop/AIprojects/MultiMonMedia/Hap/5sec_hap.mov   # also add to ~/.zshrc
dotnet build MultiMon.Core MultiMon.Hap MultiMon.Core.Tests -nologo
dotnet test MultiMon.Core.Tests --nologo
```

Expected: **0 warnings, 0 errors; 126 passed, 0 failed.** (Without the fixture the two HAP fixture tests
no-op and the count is still 126.)

Do **not** run `dotnet build MultiMon.sln` on the Mac: seven projects are `net9.0-windows` (WPF, Vortice,
CsWin32, WMI) and cannot build here, by design (`docs/MAC_PORT.md`, "Rules"). Section 4 creates a Mac solution.

If this gate is red, stop and fix the toolchain. Nothing below is valid until it is green.

---

## 3. TODO 0 — install the Claude Code routing (before any dev work)

The model/effort routing that ran the Windows audit is **global** config in `~/.claude/` and is not on this
Mac yet. Without it every task runs on the main session, and the project `CLAUDE.md` ("Model Selection
Policy") refers to agents that do not exist. Install it from `mac-handoff/`; if that folder is missing,
create the files from Appendix A–C — they are the same content verbatim.

```bash
mkdir -p ~/.claude/agents ~/.claude/skills/modelswitcher
cp mac-handoff/agents/*.md ~/.claude/agents/
cp mac-handoff/skills/modelswitcher/SKILL.md ~/.claude/skills/modelswitcher/
# Global CLAUDE.md: copy whole if absent; otherwise append the "Model & Effort Routing" section
# (Appendix B) to the existing file.
[ -f ~/.claude/CLAUDE.md ] || cp mac-handoff/global-CLAUDE.md ~/.claude/CLAUDE.md
ls ~/.claude/agents            # expect: chore.md implement.md research.md scan-sonnet.md verify.md
```

Merge these three keys into `~/.claude/settings.json` (create the file if absent). They are the exact
values from the Windows box (`mac-handoff/settings.routing.json`):

```json
{
  "model": "claude-fable-5-1[1m]",
  "effortLevel": "medium",
  "env": { "CLAUDE_CODE_SUBAGENT_MODEL": "sonnet" }
}
```

**The routing, hardcoded** (operative table; full text in Appendix B):

| Agent | Model, effort | Use for |
|---|---|---|
| main session | Fable 5.1, medium | orchestrate, plan, decide, synthesise, small direct edits |
| `implement` | Opus, high, background | one bounded multi-file slice; re-run a failed slice with `model: fable` on that call only |
| `verify` | Opus, high | fresh-context review of a diff against acceptance criteria; read-only plus tests |
| `research` | Sonnet, medium, background | reading-heavy investigation; returns a cited summary |
| `chore` | Sonnet, low | renames, moves, bulk edits, log triage |
| `scan-sonnet` | Sonnet, medium | fixed-rubric scans |
| `stability-core` (project, `.claude/agents/`) | Fable | render / decode / present / teardown core and hard native bugs — on the Mac: `MultiMon.Graphics.Mac`, `MultiMon.Decode.Mac`, the Mac controller |
| `planner` (project) | Opus | architecture, ADRs, milestone plans |
| `reviewer` (project) | Sonnet | post-change review |

Rules: delegate when work would pull many files into context, can run in parallel, or needs a fresh mind;
stay inline for one or two open files, a lookup, a decision, or ambiguous scope. Give every agent the reason
and the acceptance criteria; ask for its fixed report shape, never file contents. Fire independent agents in
one message. Effort ceiling is **high** — never xhigh / max / ultracode (user decision 2026-09-05). Invoke the
`modelswitcher` skill only to plan a fan-out of four or more agents. Isolate parallel agent work in git
worktrees (`isolation: "worktree"`) with strict file ownership and merge afterwards, as the Windows audit did.

**Check:** open Claude Code in the project, run `/agents` and confirm all eight names above are listed;
invoke `modelswitcher` with `plan this phase` and confirm it loads. Also confirm the project-level pieces
came through the zip: `.claude/agents/{stability-core,planner,reviewer}.md`,
`.claude/rules/{graphics-core,decode-threading,hap-playback}.md`, `.claude/settings.local.json` (124 allow
rules — the `dotnet` / `git` / `cat` allowlist; the `taskkill` entries are inert on macOS and can stay).

Do not start section 4 until this check passes. Record "routing installed" in `notes/CURRENT_STATUS.md`.

---

## 4. TODO 1 — Mac solution and project scaffold

Create `MultiMon.Mac.sln` beside `MultiMon.sln`. It holds the three portable projects plus the new Mac
projects. `MultiMon.sln` is never edited — Windows must keep building.

| New project | TFM | Fills | References |
|---|---|---|---|
| `MultiMon.Platform.Mac` | `net9.0-macos` | `IMonitorService` via `NSScreen`; GPU name/vendor via `MTLDevice` | Core |
| `MultiMon.Graphics.Mac` | `net9.0-macos` | ONE `MTLDevice`; ONE `CAMetalLayer`-backed borderless `NSWindow` per screen, persistent; `QuadPipeline` twin (MSL fullscreen quad + UV sub-rect + HapQ YCoCg shader); `DecodedFrame` carrying an `MTLTexture`; `FrameTimeline` (copied, pure C#); one render thread; display-link pacing | Core |
| `MultiMon.Decode.Mac` | `net9.0-macos` | `HapSource` twin (upload `MultiMon.Hap` BCn bytes to `MTLTexture`; BC1/3/4/7 work on every Mac), then `VideoToolboxSource` (`VTDecompressionSession` → `CVPixelBuffer` → `CVMetalTextureCache`, zero copy; VideoToolbox supplies the software fallback) | Core, Hap, Graphics.Mac |
| `MultiMon.Audio.Mac` | `net9.0-macos` | `IAudioEngine`: one CoreAudio / `AVAudioEngine` output per track, clocked to `MasterClock`; `AudioRing` ported (portable in shape) | Core |
| `MultiMon.Control.Mac` | `net9.0-macos` | the Mac `PerformanceController` (worker-thread command queue, same shape as Windows) + an **Avalonia** control panel over the existing `IPerformanceController` view-model contract | all of the above |
| `MultiMon.Stress.Mac` | `net9.0-macos` | headless harness: same flags, same 50-cycle zero-growth gate, resource count from Metal's validation layer | all of the above |

Scaffold (names matter — `CLAUDE.md` and the harness refer to them):

```bash
dotnet new sln -n MultiMon.Mac
for p in Platform Graphics Decode Audio; do dotnet new classlib -n MultiMon.$p.Mac -f net9.0; done
dotnet new console -n MultiMon.Stress.Mac -f net9.0
# edit each new .csproj: <TargetFramework>net9.0-macos</TargetFramework>
#                        <SupportedOSPlatformVersion>12.0</SupportedOSPlatformVersion>
dotnet sln MultiMon.Mac.sln add MultiMon.Core MultiMon.Hap MultiMon.Core.Tests MultiMon.*.Mac
dotnet build MultiMon.Mac.sln -nologo        # gate: 0 warnings, 0 errors with the empty projects
```

Add the Avalonia project last: `dotnet new install Avalonia.Templates; dotnet new avalonia.app -n MultiMon.Control.Mac`.

Then add a **macOS** subsection to `CLAUDE.md` "Build & Run" with these commands and list the `.Mac`
assemblies in "Solution structure". Commit on a `mac-port` branch: `Scaffold the macOS solution`.

---

## 5. Windows → Mac command map (for every `CLAUDE.md` line that names a Windows tool)

| `CLAUDE.md` says | On the Mac |
|---|---|
| `taskkill /F /IM MultiMon.Control.exe` | `pkill -f MultiMon.Control.Mac \|\| true` |
| `dotnet build MultiMon.sln` | `dotnet build MultiMon.Mac.sln` |
| `dotnet run --project MultiMon.Stress -- …` | `dotnet run --project MultiMon.Stress.Mac -- …` (same flags) |
| `dotnet test MultiMon.Core.Tests; dotnet test MultiMon.Tests` | `dotnet test MultiMon.Core.Tests` only (`MultiMon.Tests` is Windows-bound; a `MultiMon.Tests.Mac` arrives with the audio-ring port) |
| `Start-Process "…\MultiMon.Control.exe"` | `dotnet run --project MultiMon.Control.Mac` (or open the built `.app`) |
| `%LOCALAPPDATA%\MultiMon\Logs\` | `~/Library/Logs/MultiMon/` — make `FileLog` resolve it on macOS (Core change; keep it platform-type-free) |
| D3D11 debug-layer live-object count | Metal validation (`MTL_DEBUG_LAYER=1 MTL_SHADER_VALIDATION=1`) + a tracked-resource counter in `Graphics.Mac` + `MTLDevice.currentAllocatedSize` |
| `DEVICE_REMOVED` recovery | no equivalent on a healthy device; keep the recovery *sequencing* (`DeviceRemovedHandler` shape) for eGPU unplug (`MTLDevice` removal notifications) |

---

## 6. Dev-phase TODO list (in order; each ends in a checkable gate)

- [x] **TODO 0 — Install routing** (section 3). Gate: `/agents` lists all eight; `modelswitcher` loads.
      *If `mac-handoff/` is missing, hardcode from Appendix A–C.*
- [x] **TODO 1 — Scaffold `MultiMon.Mac.sln`** (section 4). Gate: builds empty, 0 warnings; `MultiMon.Core.Tests` still 126/126.
- [x] **TODO 2 — `MultiMon.Platform.Mac`: `NSScreen` monitor service.** `MonitorInfo.DeviceId` = the
      `CGDirectDisplayID` as a string (an opaque key — all `ShowPlanner` does with it); `Bounds` in pixels,
      matching the Windows contract — **origins:** AppKit global points × the *primary* screen's backing scale,
      Y flipped about the primary's top edge; **sizes:** each screen's own points × its own backing scale
      (one uniform unit for positions, else a Retina + non-Retina pair overlaps and Span collapses to 1×1);
      `RefreshRate` = the *current* `CGDisplayMode` rate (not `MaximumFramesPerSecond`); `IsPrimary` = the screen
      with the menu bar; `MonitorsChanged` from `NSApplication.didChangeScreenParametersNotification`.
      Gate: a small console program lists every attached display with correct pixel bounds; a `ShowPlanner`
      Span plan over that list matches the Windows plan for the same geometry (add it as a
      `MultiMon.Core.Tests` case using the real values).
- [x] **TODO 3 — `MultiMon.Graphics.Mac`** (`stability-core` agent). One `MTLDevice`
      (`MTLCreateSystemDefaultDevice`; on multi-GPU Macs pick the device driving the most outputs and log the
      candidates — the Windows adapter rule). One borderless `NSWindow` + `CAMetalLayer` per screen, created
      ONCE, shown/hidden per perform, never recreated. Render loop on one dedicated thread;
      `nextDrawable` with `displaySyncEnabled`; `FrameTimeline` copied from Windows. Port the fullscreen quad
      + UV sub-rect + HapQ shader from `MultiMon.Graphics/QuadPipeline.cs` HLSL to MSL.
      Gate: `MultiMon.Stress.Mac --cycles=50 --windows=N` on the test pattern: zero wedge, zero sustained
      resource growth, flat RSS.
- [x] **TODO 4 — `MultiMon.Decode.Mac`, HAP first.** `HapSource` twin: `MultiMon.Hap` decodes, upload via
      `replaceRegion` (`bc1_rgba`, `bc3_rgba`, `bc4_rUnorm`, `bc7_rgbaUnorm`), pooled buffers returned in the
      frame's release closure, same length/format validation as Windows.
      Gate: harness `--hap --mode=span` 50 cycles PASS; HAP visibly playing on two screens.
      *Done 2026-09-06.* The 5sec fixture is greyscale; `--source-check --hap --video=…/Hap/colour_hapq.mov` (HAP-Q
      testsrc2, generated with ffmpeg) is the chroma gate — the check NOTEs when a clip cannot prove chroma.
- [ ] **TODO 5 — `MultiMon.Decode.Mac`, VideoToolbox.** `VTDecompressionSession` with
      `kCVPixelBufferMetalCompatibilityKey`; `CVMetalTextureCache` for zero copy; BGRA output for parity with
      the Windows pass (NV12 + a YUV→RGB MSL pass only if measured to matter); assert the first frame's
      format/size like Windows does and log the decode path.
      Gate: harness `--mode=individual --video --video2` 50 cycles PASS; 4K H.264 and HEVC play.
- [ ] **TODO 6 — `MultiMon.Audio.Mac`.** Port `AudioRing` (pure); `AVAudioEngine` or `AudioUnit` output per
      track; drift correction against `MasterClock` (copy the one-shot proportional policy from
      `WasapiOutput`); device selection by `AudioTrack.OutputDeviceId`; device-invalidated rebuild.
      Gate: harness `--audio` 30 cycles, 0 underruns, peak drift under 40 ms.
- [ ] **TODO 7 — Mac `PerformanceController` + `MultiMon.Stress.Mac --controller`.** Same shape as
      Windows: one worker thread, `BlockingCollection<Action>` FIFO, UI posts and returns, `StateChanged` /
      `CommandFailed` events, `ShowPlanner` for the mapping (no port needed), symmetric decode ladder (`.mov`
      probed on HAP first). **Never** run native teardown on the AppKit main thread — LESSON-TEST-004 and the
      V0087 deadlock rule apply verbatim.
      Gate: `--controller` 50 cycles PASS in Span, Individual, Split, HAP and audio.
- [ ] **TODO 8 — Avalonia control panel.** Reuse `MainViewModel`'s contract (WPF today; lift the view-model
      logic into a portable `MultiMon.Control.Shared` if the Dispatcher seam is the only WPF dependency —
      check first). Monitor rows, mode picker, Identify overlay, mixer, New/Open/Save, Convert-to-HAP
      (ffmpeg via a Homebrew path probe). Gate: the user's manual run of every mode.
- [ ] **TODO 9 — Ship gate.** Full matrix on Apple Silicon **and** one Intel Mac; write
      `docs/sessions/<date>-mac-checkpoint.md`; update `notes/CURRENT_STATUS.md`; ADR 0005 "macOS platform
      layers" in `docs/adr/` recording the stack choices actually made.

Windows stays green throughout: never add a platform type to `MultiMon.Core` or `MultiMon.Hap`; any Core
change must keep `MultiMon.Core.Tests` at ≥126 passing and must be built on Windows before it reaches
`master` (the user runs that side).

Branching: work on `mac-port` off `master`; merge to `master` only after the user's manual check, per the
existing feedback rule.

---

## 7. Guardrails that carry over unchanged

- Persistent pipeline: device, layers, windows, shaders and state built ONCE per session. Perform = show/hide
  + rebind. Teardown only at app exit, off the UI thread, ordered: stop decode → join → dispose sources → device.
- One render thread owns every Metal command buffer and every present. Decode threads publish into
  `FrameTimeline`. The AppKit main thread never blocks on native work.
- No GPU abstraction layer, no interface over Metal objects (ADR 0001). `Graphics.Mac` is a sibling of
  `Graphics`, not a backend behind it.
- "Wedge on the Nth cycle" = ownership bug, never timing. No `Thread.Sleep` / `GC.Collect` as a fix.
- Done = harness green **and** a manual run confirmed. Reviewer mode on your own diff; the `verify` agent for
  risky slices.
- Read `notes/lessons.md` (LESSON-TEST-004 above all) and `CRASH_INVESTIGATION.md` before any wedge debugging.

## 8. Where things are

`REBUILD_ARCHITECTURE.md` (ADR + build plan) · `docs/adr/0001–0004` · `docs/MAC_PORT.md` (seams, stack, order)
· `docs/sessions/2026-09-04-full-audit.md` (what was fixed and why) · `notes/CURRENT_STATUS.md` (restart
package) · `notes/TODOs.md` (Windows TODOs — add section 6 to it) · `notes/lessons.md` ·
`MultiMon.Graphics/QuadPipeline.cs` + `FullscreenQuadPass.cs` (shader math to port) ·
`MultiMon.Control/PerformanceController.cs` (controller shape to mirror) · `MultiMon.Stress/StressHarness.cs`
(harness to mirror).

---

## Appendix A — global agent files (`~/.claude/agents/*.md`, verbatim)

### `~/.claude/agents/implement.md`

````markdown
---
name: implement
description: Builds one bounded, well-specified code change end to end (read, edit, build, test, report). Use for multi-file feature or fix slices the orchestrator hands off. Opus 5 at high effort, runs in the background. Re-run a slice that failed verification with model fable on that call only.
model: opus
effort: high
background: true
---

You implement one bounded slice of work for an orchestrator that is holding the wider plan. You are not the planner. Build exactly the slice described, nothing wider.

Before the first edit:
- Read the repository's CLAUDE.md and follow it. Its rules on event handlers, build output directories, commit hygiene and accessibility override any habit of yours.
- Read every file you will change. Do not edit from memory of how a file "usually" looks.
- Restate the acceptance criteria you were given in one line. If none were given, write the checkable criteria yourself and build to them.

While working:
- Tests first where the project practises TDD. Run the build and the relevant tests before reporting.
- Delete replaced code; never comment it out or leave a parallel path.
- Do not tidy, refactor or reformat anything outside the slice. If you notice a problem outside it, note it in the report instead of fixing it.
- Do not commit unless the task explicitly says to.

Report back in this shape, with only what the orchestrator needs to act, no file contents:
- Done: what changed, as a list of files with one line each.
- Verified: the exact build and test commands you ran and their results. If something failed, say so with the error, not a paraphrase.
- Not done / found: anything left out and why, plus problems noticed outside the slice.
- Open questions the orchestrator must decide.
````

### `~/.claude/agents/verify.md`

````markdown
---
name: verify
description: Fresh-context reviewer that grades a diff or a finished slice against its acceptance criteria, hunting bugs, scope creep, accessibility regressions and unverified claims. Read-only plus running tests. Opus 5 at high effort. Keeps its own memory of recurring defects.
model: opus
effort: high
memory: user
tools: Read, Glob, Grep, Bash
---

You are a senior reviewer who intends to reject the work unless it earns a pass. You did not write it and you do not share the author's context, which is the point.

You will be given the acceptance criteria and a way to identify the change (a diff, a branch, or a file list). Without both, say so and stop.

The repository's CLAUDE.md is part of the acceptance criteria whether or not the task restated it. Your verdict rests on the whole change read in context, not a sample, and on your own run of the project's build and tests: the author's claim that tests passed is not evidence. Beyond correctness, the verdict covers reachability (every new function is called from somewhere that runs; this codebase has shipped tested code with no caller), scope creep, dead code and parallel paths left behind, and any accessibility regression (WCAG 2.2 AA, keyboard, screen reader) in UI changes.

Verdict format, as brief as the findings allow:
- PASS or FAIL, one line, with the single most important reason.
- Findings, most severe first: file:line, what is wrong, what a failing input or scenario looks like, one-line fix hint. Only findings you have confirmed by reading or running something. Mark anything you could not confirm as PLAUSIBLE.
- Verified: commands run and results.
- Criteria not checkable here, and why.

Memory: after each review, note in your memory any defect pattern you have now seen more than once across projects, in one line each. Read that memory at the start of each review.
````

### `~/.claude/agents/research.md`

````markdown
---
name: research
description: Reading-heavy investigation that would otherwise flood the orchestrator's context: tracing how something works across many files, comparing options, reading docs or the web, answering "how does X get to Y". Returns a compact summary with citations. Sonnet 5 at medium effort, runs in the background.
model: sonnet
effort: medium
background: true
tools: Read, Glob, Grep, Bash, WebFetch, WebSearch
---

You investigate on behalf of an orchestrator whose context must stay small. Your value is the conclusion, not the trail. Read as much as the question needs; report as little as the answer needs.

- You will be told what is being decided and why. Answer that question. Do not widen it.
- Prefer primary sources: the code itself, official docs, the running service. Say when something is inferred rather than observed.
- Cite locations as file:line or URL so the orchestrator can jump straight there.
- If the question cannot be answered from what you can reach, say what is missing rather than guessing.

Report, as short as the answer allows and no shorter:
- Answer: the conclusion in one to three sentences.
- Evidence: the locations and what each shows, as a list.
- Caveats: what you could not confirm, and anything that contradicts the answer.
- Suggested next step, one line, only if one is obvious.
````

### `~/.claude/agents/chore.md`

````markdown
---
name: chore
description: Mechanical, well-defined work with a fixed outcome: renames, import updates, bulk string replacements, moving files, log or test-output triage, applying a formatter. Sonnet 5 at low effort. Not for anything that needs a judgement call.
model: sonnet
effort: low
---

You do mechanical work exactly as specified. The task has a fixed, checkable outcome; your job is to reach it and prove you did.

At low effort you will be tempted to work from memory. Do not:
- Never edit a file you have not read in this session.
- Never assume a symbol, path or setting exists; grep for it first.
- Never widen the task. If the specification turns out to need a judgement call, stop and report the question instead of deciding it.
- Read the repository's CLAUDE.md before editing anything, and follow it.

When done, run whatever check proves the outcome (a build, a grep showing zero remaining matches, a test run) and report:
- Done: files touched, one line each.
- Proof: the command you ran and its result.
- Skipped or unclear: anything you could not do mechanically, with the question for the orchestrator.
Keep the report to the outcome and its proof. No file contents.
````

### `~/.claude/agents/scan-sonnet.md`

````markdown
---
name: scan-sonnet
description: Fast rubric-driven scan of a codebase against a fixed checklist or pattern list (audit checklists, grep-style security patterns, wiring checks, lint-like sweeps). Read-only. Sonnet 5 at medium effort. The cost comparison variant; use when raw speed on a fixed-shape task matters more than judgement.
model: sonnet
effort: medium
background: true
tools: Read, Glob, Grep, Bash
---

You run a scan against a fixed rubric supplied in the task. Enumerate and match; do not reinterpret the rubric or add criteria of your own.

- Cover the whole scope given. Say explicitly which directories or files you scanned.
- Report only what you observed at a specific location. No speculation about code you did not open.
- Use the severity definitions in the task. If none were given, use BLOCKER / HIGH / MEDIUM / LOW and say you defaulted.

Output, in this exact shape:
Findings:
- [severity] file:line: one-line problem; one-line fix hint
Summary: N blocker, N high, N medium, N low; scope covered; anything skipped and why.
Every finding earns a line; nothing else does.
````

## Appendix B — global `~/.claude/CLAUDE.md` (verbatim; the routing section is the part that matters)

````markdown
# Global Claude Code Rules

## Tool Use — Always Prefer Acting Over Asking

**NEVER ask the user to run a command you can run yourself.**

Use Bash, PowerShell, or other tools directly. If a setup step requires running a script, starting a server, installing a package — do it. Only ask the user to take action when it genuinely requires their input (browser interaction, credentials, approval of a destructive operation).

Examples of what to do instead of asking:
- `npm install` → run it with Bash
- `node auth.js` → run it in the background with Bash (run_in_background: true) then give the user the next step
- `git status` → run it
- Starting a dev server → run in background, monitor output, report result

## Karpathy Guidelines (default)

Apply the `karpathy-guidelines` skill by default for coding work. If the plugin is not
installed, install it once (these `/plugin` commands are interactive — run them yourself):
- `/plugin marketplace add forrestchang/andrej-karpathy-skills`
- `/plugin install andrej-karpathy-skills@karpathy-skills`

## Model & Effort Routing (always on)

The main session is the orchestrator: Fable 5.1 at medium effort. It plans, decides, synthesises, and does small direct work itself. Larger work routes to a named agent from `~/.claude/agents/`, and those agents run on Opus and Sonnet so Fable tokens go only to orchestration. Do not invoke `modelswitcher` for routine routing; use it only to plan a fan-out of four or more agents.

**Delegate when** the work would pull many files into this context, can run in parallel, or needs a fresh mind (verification). **Stay inline when** it is one or two files already open, a fact lookup, a decision, ambiguous scope, or work that mixes research and edits. Keep inline work short: long tool-heavy turns on the orchestrator are the most expensive thing in this setup.

| Agent | Model, effort | Use for |
|---|---|---|
| implement | Opus, high | a bounded multi-file feature or fix slice; runs in the background |
| verify | Opus, high | fresh-context review of a diff against acceptance criteria; read-only plus tests |
| research | Sonnet, medium | reading-heavy investigation, docs, comparisons; returns a cited summary |
| chore | Sonnet, low | renames, imports, bulk transforms, log triage |
| scan-sonnet | Sonnet, medium | rubric scans and pattern searches against a fixed checklist |
| Explore (built-in) | Sonnet by default | locating files and symbols |

Rules: give every agent the reason for the task and the acceptance criteria. Ask for a report the orchestrator can act on without opening files, in the agent's fixed report shape, never file contents. Fire independent agents in one message. Build on Opus, verify on Opus, and re-run only a slice that failed verification, on the implement agent with `model: fable` for that call. Never run effort above high: xhigh, max and ultracode are never used (user decision 2026-09-05, overkill for this workload). Fable is never a subagent default.
````

## Appendix C — `~/.claude/skills/modelswitcher/SKILL.md` (verbatim)

````markdown
---
name: modelswitcher
description: Use when planning a fan-out of four or more subagents (audits, reviews, migrations, refactors, multi-agent workflows) to decide which agent and effort runs which task and what runs in parallel. Routine one-off routing is handled by the always-on routing rule in ~/.claude/CLAUDE.md and the named agents in ~/.claude/agents/; do not invoke this for that. Trigger on "plan this phase", "run the audit", "use tokens efficiently", "run in parallel".
---

# ModelSwitcher

A decision rubric for choosing models per task and orchestrating multi-agent work.

## Tier availability & the standing router

**Fable 5.1 is the orchestrator only; delegated work runs on Opus and Sonnet.** The main session
runs Fable at medium effort; the named agents in `~/.claude/agents/` are implement (Opus high),
verify (Opus high), research (Sonnet medium), chore (Sonnet low) and scan-sonnet (Sonnet medium).
Effort is capped at high by user decision (2026-09-04, reaffirmed 2026-09-05): xhigh, max and
ultracode are never used; they are overkill for this workload. Fable is never a subagent default; the one exception is re-running a slice that
failed verification with `model: fable` on that call. Reason (2026-09-04): Fable is metered on a
weekly allowance that a Fable-everywhere setup drained in hours; the reading and writing volume
belongs on the cheaper tiers.

Per-token prices: Fable 2x Opus, 5x Sonnet. Anthropic's notes say Fable at low effort can beat
Opus on cost per task, but that only holds when Fable finishes in fewer turns and only against an
API bill, not a capped allowance. Opus 5 at high is Anthropic's own recommended default for coding;
Sonnet 5 carries reading-heavy and fixed-shape work where effort curves are nearly flat.

| Tier | Use | Fallback |
|---|---|---|
| **Fable, medium** | orchestration only: planning, decisions, synthesis, small inline edits | Opus high |
| **Opus, high** | implementation and verification (implement, verify agents) | Fable high, failed slices only |
| **Sonnet, medium** | research, rubric scans, pattern greps, browser sweeps | Opus high |
| **Sonnet, low** | mechanical work with a fixed outcome; tell it to read before editing | Sonnet medium |
| **Haiku** | file listing, trivial bulk transforms | Sonnet low |

## Allocation matrix

| Task shape | Model | Why |
|---|---|---|
| Long-horizon agentic coding (investigate → patch → test → recover, many files, many steps) | **Opus, high** (implement agent) | The workload where higher effort measurably earns its cost |
| Multi-agent orchestration at scale (tens of parallel subagents, complex fan-out) | **Fable, medium** (main session) | Fable 5.1 sustains long-running parallel subagents reliably; delegate asynchronously |
| Rubric-driven scans, checklists, audits against a fixed pattern list | **Sonnet** | Enumeration + pattern matching — high-effort reasoning is wasted |
| Pattern-matching (SQL injection grep, prepared-statement check, JWT verification, CORS scan) | **Sonnet** | Well-understood patterns; low false-negative risk with a tight prompt |
| Browser-driven UX sweeps (Playwright, screenshots, console-error capture) | **Sonnet** | Mechanical interaction + observation; rarely needs deep reasoning |
| Mechanical code edits (rename, typo, CSS tweak, import update, string replace) | **Sonnet** or **Haiku** | No architectural reasoning needed; pick Haiku if it's a one-shot edit |
| File listing, status checks, simple reads, dependency listing | **Haiku** | Fast + cheap; don't pay Sonnet rates for `ls` |
| **Synthesis across multiple subagent outputs** (merge, dedupe, rank, prioritise) | **Fable, medium** (inline) | The bottleneck for quality; never delegated |
| Architectural decisions (data model, route refactor, auth redesign, RLS policy) | **Fable, medium** (inline) | Tradeoff reasoning matters |
| Debugging non-obvious bugs (races, timing, cross-cutting state) | **Opus, high** (implement), or inline on Fable medium | Hypothesis formation is where cheaper tiers are weakest |
| Refactors that change abstractions (not just rename) | **Opus, high** (implement) | Needs to see shape of the change |
| Writing net-new architecture or skill content | **Fable, medium** (inline) | Generative quality matters |

## Parallelism rules

1. **Independent subagents → single message, multiple Agent calls.** If the subagents don't share state or consume each other's output, fire them together. Prompt must include: narrow scope, the reason for the task, and an explicit output schema.
2. **Dependent work → sequential.** If agent B needs agent A's output (e.g., UX sweep needs the audit findings to know what to exercise), run sequentially.
3. **Synthesis always runs last.** Merge subagent outputs in the main session (Fable, medium) — don't delegate synthesis to a subagent, the context isolation loses nuance.

## Tradeoffs to mitigate

- **Sonnet miss rate on security severity grading:** Give Sonnet a strict severity rubric in the prompt (e.g. "BLOCKER = exploitable by anon user; HIGH = exploitable with auth"). In the synthesis pass, re-read anything Sonnet marked BLOCKER/HIGH and anything it dismissed as low that *smells* off.
- **Sonnet can under-specify fix recommendations.** Ask subagents for "file:line + 1-line fix hint", not verbose prose. The orchestrator expands into real fix plans in synthesis.
- **Subagent prompt quality is the actual lever.** The model tier matters less than the prompt. Before downgrading a task from Fable to Sonnet, rewrite the prompt to be narrower and schema-bound. A good Sonnet prompt beats a lazy Fable prompt.

## Output schema for audit subagents

Request each subagent return:

```
Findings:
- [severity] file:line — one-line problem — one-line fix hint
- ...

Summary: <N blockers, N high, N medium, N low>
Findings and the summary line only; no surrounding prose.
```

This makes the synthesis pass fast and deduplicable.

## When NOT to switch down

- If the task has **ambiguity about scope or success criteria** — stay inline on Fable, clarify with user first.
- If the task **mixes research and code changes** — stay inline on Fable; the handoff between investigation and implementation benefits from a single mind.
- If the user is **frustrated, stuck, or says "you're not understanding"** — stay inline on Fable; cheaper models compound the miscommunication.
- If it's a **destructive production action** (DELETE on prod DB, force push, production deploy) — the orchestrator plans it, Sonnet/Haiku never authored it.

## Effort level

Effort controls **thinking depth** — how much reasoning the model applies before
responding. It is orthogonal to model tier: you can run Sonnet at high effort or Fable
at low effort. Always specify both dimensions.

| Effort | When to use | Cost signal |
|--------|-------------|-------------|
| **High** | Architectural decisions, multi-file refactors, complex debugging, eval/rubric synthesis, anything where being wrong costs more than re-running. Enable extended thinking if the model supports it. | ~2–4× a normal turn |
| **Medium** | Standard feature work, code review, test writing, most Sonnet tasks. The default for day-to-day dev. | Baseline |
| **Low** | Mechanical edits (rename, format, import fix), file listing, status checks, bulk search/replace. Haiku-class at low effort is the cheapest useful unit. | ~0.1–0.3× baseline |

**Effort + model combinations:**

| Combination | Use for |
|-------------|---------|
| Fable + medium | Orchestration only: planning, synthesis, decisions, small inline edits. |
| Opus + high | Implementation and verification (implement, verify agents). |
| Fable + high | Re-running one slice that failed verification; never a default. |
| Sonnet + high | Rarely worth it: if a scan needs judgement, hand it to the verify agent (Opus, high) instead. |
| Sonnet + medium | Standard dev loop. |
| Haiku + low | Rote tasks: log grep, file listing, bulk string replace. |

**In Claude Code:** effort is set per agent in the agent file's frontmatter (`effort: low|medium|high`),
not per Agent call. The main session's effort is `effortLevel` in settings (medium). Routing to a
tier therefore means choosing the agent that carries it: implement/verify (Opus high),
research and scan-sonnet (Sonnet medium), chore (Sonnet low). Ceiling is high; xhigh, max and ultracode are never used.

**Extended thinking (API):** When building LLM products (not just using Claude Code),
recommend high-effort + extended thinking for: initial architecture decisions, security
analysis, complex reasoning chains. Keep it off for interactive loops — latency kills
UX. Budget guideline: 4000 tokens = light reasoning; 8000 = thorough; 16000+ = deep
research or hard math. Always measure whether the thinking budget actually improves
output for your specific task before committing to a high budget in production.

## Quick decision heuristic

Ask three questions:
1. **Is the output shape fixed?** Yes → scan-sonnet or chore. No → implement (Opus high), or inline on Fable medium.
2. **Would a skilled intern get this right with a checklist?** Yes → scan-sonnet / chore. No → implement or verify (Opus high).
3. **Does getting it wrong cost more than re-running at high effort?** Yes → Opus high. No → the cheaper agent.

Then add the effort dimension:
4. **Is this a one-shot mechanical action?** Yes → low effort. **Is this standard feature work?** Medium. **Is this a decision that shapes downstream work?** High.

If the answer is mixed, plan inline on Fable at medium effort and hand the executed sub-tasks to the agent whose tier matches (implement Opus high, research Sonnet medium, chore Sonnet low).

## Usage pattern

Before launching multi-agent work, the assistant should:

1. Name the tasks and classify each (rubric / pattern / synthesis / architectural / mechanical).
2. For each task, assign both a **model tier** and an **effort level** (high / medium / low).
3. Propose the model + effort matrix to the user in a short table — let them veto before spending tokens.
4. Fire independent subagents in parallel (one message, N Agent calls), each with a narrow prompt and explicit output schema.
5. Run synthesis inline in the main session (Fable, medium).
6. For any finding that flips a decision, hand the diff to the verify agent before acting.
````
