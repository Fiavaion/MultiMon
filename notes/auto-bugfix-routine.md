# MultiMon Auto Bug-Fix Routine — Playbook

**Mode:** Semi-automatic — auto up to a PR + drafted docs; **human approves merge / release / website
publish.** Created 2026-06-19. This file is the source of truth for the scheduled cloud routine; edit it
to change the routine's behaviour.

## Trigger
- Scheduled cloud agent (cron), **daily**. Adjustable.

## Queue (what it acts on)
- Open issues on `Fiavaion/MultiMon` labelled **`bug`** — i.e. reports filed via the in-app
  "Report a bug" button or the GitHub new-issue page (both use `bug_report.yml`, which auto-applies the
  `bug` label).
- **Skip** issues already labelled `auto-pr-open`, `needs-human`, `wontfix`, or that have an assignee or
  an open linked PR. (Idempotent — never double-acts on the same issue.)
- Command: `gh issue list --repo Fiavaion/MultiMon --label bug --state open --json number,title,body,labels,assignees`

## Per-issue procedure
1. **Read** the issue (version, GPU, monitors, steps, attached log).
2. **Classify by subsystem** (the critical safety branch):
   - **Stability-critical** — anything touching `MultiMon.Graphics` (device/swapchain/render-loop),
     `MultiMon.Decode` native paths, the threading/teardown model, or any "crash / freeze / wedge on
     Nth cycle / device-removed" symptom. → **Do NOT patch.** Add a triage comment (suspected cause,
     affected files, repro notes), label **`needs-human`** + **`stability-core`**, and move on.
     *(Per CLAUDE.md model policy: the stability core + hard native bugs are Fable/human territory and
     require hardware verification a cloud agent cannot do.)*
   - **Safe to auto-fix** — WPF control panel, parsers, models, project I/O, pure logic, docs, copy.
     → proceed to step 3.
3. **Reproduce** where possible (unit test or a `MultiMon.Stress` invocation described in the issue).
   Add a failing test first when the bug is in pure logic.
4. **Fix** on a branch `autofix/issue-<n>`. Minimal, surgical change; delete-old-path discipline; no
   speculative edits.
5. **Verify (what a cloud agent CAN do):** `dotnet build MultiMon.sln -c Release` (0 warnings) +
   `dotnet test MultiMon.Tests`. The GPU **stress harness and real-hardware run cannot run in the
   cloud** — they are the human's gate before merge.
6. **Open a PR** (`gh pr create`) → base **`bugfix`** (the long-lived integration branch off `rebuild`,
   where auto-fixes accumulate for human verification before merge), head `autofix/issue-<n>`:
   - References `Fixes #<n>`.
   - Adds a CHANGELOG `[Unreleased] / Fixed` entry.
   - PR body includes **drafted launch docs**: a Pulse-post snippet + a one-line product-page note,
     ready for the human to apply at publish time.
   - PR body MUST state: *"Unit tests pass. NOT yet verified on the stress harness or real hardware —
     run `MultiMon.Stress --cycles=50` on the target GPU class before merging."*
   - Label the issue **`auto-pr-open`**; comment the PR link on the issue.
7. **If it can't fix it or tests fail:** revert the branch, comment the findings on the issue, label
   **`needs-human`**, move on. Never open a red PR.

## Hard guardrails (the routine must never)
- Never push to `master`, never merge, never tag, never create a release.
- Never publish to the live Fiavaion website or send email.
- Never run destructive git/OS operations.
- One issue per PR. Never bundle. Never touch `Old/` or `OldProjectDoNotTouch/`.
- Never claim hardware/harness verification it didn't run.

## Human approval gate — NOTHING merges until BOTH pass
Auto-fixes land on the **`bugfix`** branch (via per-issue PRs). Before merging `bugfix` → `rebuild`:
1. **Full test suite (Claude, locally):** `dotnet build MultiMon.sln -c Release` (0 warnings) +
   `dotnet test MultiMon.Tests` + **`MultiMon.Stress --cycles=50 [--mode ...]`** on the relevant GPU(s).
   The harness is the real gate and must run on real hardware — a cloud agent cannot do this.
2. **Manual app check (user):** launch the running app and confirm the fix + no regression.
3. **Only when 1 AND 2 are green → merge `bugfix` → `rebuild`** (then the normal release/tag flow).
4. Apply the drafted Pulse post + product-page note to the website and deploy (the gated publish step).

## Labels the routine uses (create once in the repo)
`bug` (auto by template), `auto-pr-open`, `needs-human`, `stability-core`, `wontfix`.

## Where docs land
- **Git:** branch + commit(s) + CHANGELOG entry + the PR (this is the git-side documentation).
- **Website:** drafted in the PR body (Pulse snippet + product-page note); applied by the human at the
  gated publish step. Optionally a follow-up PR to the website repo.
