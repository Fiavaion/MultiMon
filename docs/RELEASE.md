# Releasing MultiMon

One page. How a versioned build of MultiMon reaches users on both platforms.

Artifacts per release:

| Platform | File | Built by |
|---|---|---|
| Windows x64 | `MultiMon-<version>-win-x64.zip` | `scripts/publish-release.ps1` |
| macOS (Apple Silicon) | `MultiMon-<version>-macos-arm64.dmg` | `scripts/publish-release-mac.sh` |

CI runs both scripts on a tag push; both also run locally, unchanged.

---

## 1. Cut a release

1. **Bump the version.** `Directory.Build.props` is the single source of truth — update `<Version>`,
   `<AssemblyVersion>`, `<FileVersion>`, `<InformationalVersion>`.
2. **Update `CHANGELOG.md`** — add the new `## [X.Y.Z] - YYYY-MM-DD` section at the top. The release
   workflow takes the release-notes body from that top section.
3. **Pass the hardware gates** (section 3). They are human work and CI cannot do them.
4. **Commit** the bump on `master`.
5. **Tag and push:**
   ```bash
   git tag vX.Y.Z
   git push origin master
   git push origin vX.Y.Z
   ```
   The tag must match `<Version>` exactly, minus the leading `v` — the workflow fails fast if it does not.
6. **Watch `.github/workflows/release.yml`.** `windows-latest` builds the zip, `macos-latest` builds
   (and, with the secrets below, signs and notarizes) the DMG, and a third job attaches both to a
   **draft** GitHub release for the tag.
7. **Publish the draft by hand** once the artifacts have been downloaded and smoke-tested. Nothing is
   published automatically.

Every pull request and every push to `master` also runs `.github/workflows/ci.yml`, which builds
`MultiMon.sln` on Windows and `MultiMon.Mac.sln` on macOS with `-warnaserror`, and runs the test
projects each platform can run.

---

## 2. GitHub secrets

All six are optional. With none of them set the workflow still succeeds and produces an **unsigned**
DMG (macOS shows the "unidentified developer" warning; users right-click → Open). The Windows zip is
unsigned either way — there is no Windows code-signing certificate yet.

| Secret | What it is | How to produce it |
|---|---|---|
| `MAC_SIGN_IDENTITY` | The codesign identity string, e.g. `Developer ID Application: Fiavaion (ABCDE12345)` | On the Mac with the certificate installed: `security find-identity -v -p codesigning`. Copy the quoted name. |
| `MAC_CERT_P12_BASE64` | The Developer ID Application certificate + private key, base64 | Keychain Access → My Certificates → right-click the *Developer ID Application* certificate → Export → `.p12` with a password. Then `base64 -i cert.p12 -o cert.b64` and paste the contents of `cert.b64` as the secret value. |
| `MAC_CERT_PASSWORD` | The password chosen during that `.p12` export | — (it is also reused as the temporary CI keychain password) |
| `APPLE_ID` | The Apple ID of the developer account | — |
| `APPLE_TEAM_ID` | The 10-character Developer Team ID | appstoreconnect.apple.com → Membership, or the parenthesised suffix of the signing identity. |
| `APPLE_APP_PASSWORD` | App-specific password for notarytool | appleid.apple.com → Sign-In and Security → App-Specific Passwords → generate one named e.g. "MultiMon notarytool". Not the account password. |

Notarization runs only when `APPLE_ID`, `APPLE_TEAM_ID` and `APPLE_APP_PASSWORD` are all present;
signing runs only when `MAC_SIGN_IDENTITY` is present. The `.p12` is imported into a throwaway
keychain in `$RUNNER_TEMP` and the decoded file is deleted immediately after import.

---

## 3. What stays human — the hardware gates

CI proves the code compiles and the portable logic passes. It does **not** prove stability, and per
`CLAUDE.md` a release is not done until real behaviour has been seen working. GitHub runners have no
multi-monitor rig and no discrete GPU, so the stress harness is never the CI gate.

Before publishing the draft release:

- **Windows, per GPU class (AMD, NVIDIA, Intel/integrated):**
  ```powershell
  dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1
  dotnet run --project MultiMon.Stress -- --cycles=50 --windows=2 --controller --mode=individual --video=<clip> --video2=<clip>
  ```
  Both must pass: zero wedge, no D3D11 live-object growth across cycles, flat working set.
- **macOS (Apple Silicon):** the equivalent `MultiMon.Stress.Mac` run — see `docs/MAC_SETUP.md`.
- **A manual run of the app** on each target platform: load media, enter and leave perform mode,
  check audio and multi-monitor sync.
- **Unpack the actual CI artifacts** and launch them, rather than a local build — that is what users get.

Record the result in the session log and in `CHANGELOG.md` under "Known limitations" if any gate was
not run on real hardware.

---

## 4. Building the kits locally

Identical to what CI runs; useful for testing a kit before tagging.

```powershell
# Windows
powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1
# -> dist\MultiMon-<version>-win-x64.zip
```

```bash
# macOS (unsigned)
bash scripts/publish-release-mac.sh
# -> dist/MultiMon-<version>-macos-arm64.dmg

# macOS (signed, and notarized when the Apple variables are also exported)
export MAC_SIGN_IDENTITY="Developer ID Application: Fiavaion (ABCDE12345)"
export APPLE_ID="..." APPLE_TEAM_ID="..." APPLE_APP_PASSWORD="..."
bash scripts/publish-release-mac.sh
```

`dist/` is git-ignored. FFmpeg is not bundled in either kit; it is an external prerequisite for the
Convert-to-HAP feature only.

## Mac session — first things after this merge lands

Do these on the Mac, in order, and record results in `notes/CURRENT_STATUS.md`:

1. `git checkout master && git pull` — the Mac projects now live on master; `mac-port` is finished and can be deleted.
2. Build and test from master exactly as CI does: `dotnet build MultiMon.Mac.sln -c Release -warnaserror`, then
   `dotnet test MultiMon.Core.Tests -c Release`. Then the Mac stress harness on real hardware (the human gate CI cannot run).
3. First real run of `scripts/publish-release-mac.sh` (unsigned first, no env vars). Compare its DMG with the one you built by hand
   for `v1.0.0-rc.1-mac`: app launches, icon present, `Stress/` layout, size. Fix the script until they match; commit the fixes.
4. Signing: run it again with `MAC_SIGN_IDENTITY` set, then with the `APPLE_*` variables for notarization. Note whether the hand-built
   DMG used `--deep` or inside-out signing and make the script match; decide whether `Stress/` should be in the public DMG at all.
5. Export the Developer ID Application certificate as `.p12`, base64 it, and give the repo owner the six secrets named above so
   `release.yml` can sign in CI. Until then CI produces an unsigned DMG and the job still succeeds.
6. Rebuild 1.0.0 from the `v1.0.0` tag with the script and replace the DMG asset on the GitHub release, so the shipped Mac build
   reports 1.0.0 rather than rc.1 in About.
7. From then on: never build a release by hand. Bump `Directory.Build.props` + `CHANGELOG.md`, tag, let CI build both, publish the draft.
