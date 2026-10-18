# Two machines, one branch

Since the 2026-10-18 merge of `merge/mac-port`, `master` holds both platforms. This page says how the
Windows PC and the Mac share it.

## What each machine builds

| Machine | Checkout | Solution | Tests it can run | Human gate |
|---|---|---|---|---|
| Windows PC | `D:\AIprojects\MultiMon`, `master` | `MultiMon.sln` | `MultiMon.Core.Tests` + `MultiMon.Tests` | `MultiMon.Stress` + a manual run |
| Mac | a `git clone` of github.com/Fiavaion/MultiMon, `master` | `MultiMon.Mac.sln` | `MultiMon.Core.Tests` | `MultiMon.Stress.Mac` + a manual run |

The two solutions share only the portable projects: `MultiMon.Core`, `MultiMon.Hap`, `MultiMon.Core.Tests`.
Neither solution lists the other platform's projects, so the Windows build never needs the macOS workload
and the Mac build never needs WPF or Vortice. `Directory.Build.props` (version) and `CHANGELOG.md` are
shared too.

## Day-to-day

1. Start from an up-to-date `master` (`git checkout master && git pull`).
2. Work on a short branch named per `CONTRIBUTING.md` (`fix/…`, `feat/…`). Mac work uses the same
   pattern (e.g. `feat/mac-notarize`); there is no long-lived `mac-port` branch any more.
3. Push the branch and open a pull request into `master`. `.github/workflows/ci.yml` builds
   `MultiMon.sln` on Windows and `MultiMon.Mac.sln` on macOS with `-warnaserror` and runs the tests each
   platform can run. A change made on one machine that breaks the other platform fails here, before it
   reaches `master`.
4. Run your own platform's human gate (stress harness + manual run) before merging. CI cannot run it.
5. Merge, then the other machine pulls `master`.

## Rules that keep the two paths apart

- Never add a platform type to `MultiMon.Core` or `MultiMon.Hap` (from `docs/MAC_SETUP.md`).
- A change to a shared project needs CI green on **both** runners before it merges; if it changes
  behaviour on the other platform, that platform's human gate runs too.
- Platform-only code stays in its own projects: `*.Mac` projects on the Mac side, the existing Windows
  projects on the Windows side.

## Leftovers from the port (status at the merge)

- **`mac-port` and `merge/mac-port`:** fully contained in `master` after the merge. Delete them on
  GitHub and locally once `master` is pushed. Pushing `master` also marks PR #1 as merged.
- **`D:\AIprojects\MultiMon-mac-transfer`:** not a git repository. It holds
  `MultiMon-mac-transfer-2026-09-06.zip`, a one-off snapshot used to start the port on the Mac; every
  git ref in it already exists in this repository, so it carries no unmerged work and is superseded by
  cloning from GitHub. `MultiMon-test-media.zip` is test media that git does not carry: keep it (a copy
  also sits in the git-ignored `mac-handoff/`).
- **Tag `v1.0.0`** sits on `63d62b1`, before the merge, so it contains no Mac source. The shipped DMG was
  built from tag `v1.0.0-rc.1-mac`. Future releases of both platforms are cut from one tag on `master`
  (`docs/RELEASE.md`).
