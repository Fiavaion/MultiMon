# Crash Investigation: Perform Mode Crash on Repeated Launch

## Problem Description
App crashes when launching perform mode multiple times. User loads 2 videos on 2 monitors, plays (perform mode), exits with Escape, then plays again. Crash occurs on Nth attempt.

## Test Configuration
- 2 monitors (DISPLAY1, DISPLAY3)
- 2 test videos: CosmicPower_15Sec.mp4, greenCrystals_15Sec.mp4
- No audio tracks loaded (isolated to video playback)

## Timeline of Investigation

### Initial State (Before Fixes)
- **Crash Point**: 2nd perform mode launch
- **Symptoms**: Black screen on one monitor, then crash after a few seconds

### Key Findings from Log Analysis

#### Finding 1: LibVLC State Persistence (CONFIRMED)
Comparing 1st play vs 2nd play logs:
```
1st play: PLAY CALLED - Video: CosmicPower_15Sec.mp4, Volume: 100, Mute: False
2nd play: PLAY CALLED - Video: CosmicPower_15Sec.mp4, Volume: 100, Mute: True  <-- WRONG!
```
**Root Cause**: Shared LibVLC instance retains mute/volume state from disposed MediaPlayers.
During shutdown, we set `_mediaPlayer.Mute = true; _mediaPlayer.Volume = 0;` which persisted.

#### Finding 2: Crash Timing
Crash occurs shortly after:
1. MEDIA OPENED events fire for both windows
2. WINDOW READY FOR SYNC fires for both windows
3. Sync timer starts processing (~300ms after MediaOpened)

#### Finding 3: Missing Events on Crash Cycle
On the crashing cycle (4th try after fixes), only ONE "MEDIA PLAYING" event fired instead of two.
This suggests second window didn't fully initialize before crash.

## Fix Attempts

| Fix # | Description | Files Changed | Result | Crash Moved |
|-------|-------------|---------------|--------|-------------|
| 1 | Added `_isDisposed` checks to VideoWindow properties | VideoWindow.xaml.cs | No change | 2nd -> 2nd |
| 2 | Added `IsReadyForSync` flag with 300ms delay after MediaOpened | VideoWindow.xaml.cs, WindowManager.cs | No change | 2nd -> 2nd |
| 3 | Reset Mute=false, Volume=100 after creating MediaPlayer | VideoWindow.xaml.cs | IMPROVED | 2nd -> 4th |
| 4 | Changed shutdown from Mute=true to Stop() | VideoWindow.xaml.cs | Part of #3 | - |
| 5 | Increased cleanup delay 300ms->500ms, added GC.Collect | WindowManager.cs | IMPROVED | 4th -> 6th |
| 6 | Added 50ms delay after MediaPlayer.Dispose() | VideoWindow.xaml.cs | IMPROVED | 4th -> 6th |
| 7 | Removed Stop() from disposal, added step logging, increased delays | VideoWindow.xaml.cs | Helped | 6th -> 5th |
| 8 | Added auto-reset feature: LibVLC reset after N cycles | LibVLCProvider, WindowManager, Settings | Partial | Crash at 4 plays (was counting 2x) |
| 9 | Fixed double-counting, reduced threshold to 3 | WindowManager.cs, AppSettings.cs | Partial | 5th play (2nd after reset) |
| 10 | Reduce threshold to 2 (reset before every 2nd play) | AppSettings.cs | WORSE | 3rd play (1st after reset!) |
| 11 | Disable auto-reset, increase cleanup delay to 1500ms | AppSettings, WindowManager | **BEST** | 5th -> 9th! |
| 12 | REMOVE all cycle counting/reset code entirely | WindowManager, IWindowManager, MainViewModel, MainWindow | WORSE | 4th play (was 9th!) |
| 13 | Increase cleanup delay to 2500ms | WindowManager.cs | PENDING | TBD |
| 14 | Fix Media disposal order - dispose Media BEFORE MediaPlayer | VideoWindow.xaml.cs | FAILED | Still crashed at MediaPlayer.Dispose() |
| 15 | Call MediaPlayer.Stop() explicitly before disposal | VideoWindow.xaml.cs | **SUCCESS** | No crash! 2+ cycles ✓ |
| 16 | Gate sync on Playing state (Fix A) + stop playback before view detach (Fix B) (V0082) | VideoWindow.xaml.cs, WindowManager.cs | Fix A WORKED (no mid-playback sync crash, 10s playback clean); Fix B REGRESSED — crashed cycle 1 inside `_mediaPlayer.Stop()` while D3D9 vout still attached | mid-playback (c5) -> disposal (c1) |
| 17 | Revert Fix B; keep Fix A only. Restore detach-only `Stop()` (V0083) | VideoWindow.xaml.cs | Still wedged ~cycle 4 — Fix A insufficient | TBD |
| 18 | **Teardown OFF the UI thread (`BeginShutdown`) (V0087)** | VideoWindow.xaml.cs, WindowManager.cs | Proven in stress harness (20/20 fullscreen cycles vs deadlock@1); PENDING manual confirm | — |

### THE ROOT CAUSE — found V0086 via stress harness + crash dumps

After 17 fixes chasing a "shared-instance accumulation" ghost, a controlled harness (`StressHarness.cs`,
run via `MultiMon.exe --stress`) plus two full crash dumps (`dotnet-dump`) settled it:

**It was never lifecycle accumulation.** The harness ran **60 bare MediaPlayer lifecycles windowed on
the primary monitor with ZERO wedge**. The shared LibVLC instance does not accumulate fatal state.

**It is a UI-thread teardown deadlock, triggered by the fullscreen secondary-monitor D3D9 vout.**
- Crash dump (hang): UI thread blocked in `libvlc_event_detach` (app) / `libvlc_media_player_stop`
  (harness), called from window teardown.
- Mechanism: `libvlc_media_player_stop`/`event_detach` block until the D3D9 vout thread finishes;
  that vout thread needs the **UI message loop** to complete shutdown — so running teardown on the
  UI thread self-deadlocks. The small windowed vout tears down without that handshake; the
  fullscreen secondary-monitor vout does not. Sometimes it presents as a hang, sometimes the same
  wedged state escalates to a native AV (the "crash"). Either window can be the victim because the
  setup always spans two physical monitors — the harness's clean run never used the 2nd monitor.

**Harness proof of the fix:** identical fullscreen-2-monitor config, `--bgteardown`:
- UI-thread teardown → **deadlock at cycle 1**
- Background-thread teardown → **20/20 cycles clean, clean exit**

**Fix #18 (V0087):** `VideoWindow.BeginShutdown()` detaches the view on the UI thread, runs
Stop/event-detach/Dispose on a background thread (UI loop stays free to pump), then self-closes the
window. `WindowManager.CloseAllWindows()` calls it instead of `window.Close()`. Removed the
cargo-cult 1500ms sleep+GC. Fixes #1–#17 were all masking a deadlock they never located.

### Fix #16/#17 detail (2026-06-10) — sync-into-Opening-player is the real root cause

**Verified root cause of the historical "Nth-cycle" crash** (multi-agent investigation, V0082):
The crash was NEVER in disposal — 15 prior fixes looked in the wrong place. Both log writers are
`AutoFlush=true` (App.xaml.cs:192, FileLoggingService.cs:51), so a missing log line = code that
never ran. On the crash cycle, the log dies mid-playback inside the sync loop, BEFORE any Escape/
disposal line. Mechanism: `_readyForSync` was armed on a blind 300ms timer after MediaOpened, NOT
on the Playing state, so the 60Hz sync timer would `Seek`/`SetSpeedRatio` a 2nd window still stuck
in Opening (visually "loading") — calling set_time/set_rate on a player whose input/vout thread is
still spinning up is a native AV. Always the 2nd window because it's the one that lags into Opening.

**Fix A (kept, V0082+):** arm `_readyForSync` only in `OnMediaPlaying`, and make `IsReadyForSync`
live-check `State == Playing`. The sync loop can no longer touch a non-Playing player. A window
stuck on "loading" becomes a harmless visual glitch instead of a crash.

**Fix B (REVERTED, V0083):** added `_mediaPlayer.Stop()` to `VideoWindow.Stop()` *before* detaching
the view, intending to stop the per-cycle DXVA2 decoder leak. This REGRESSED to a cycle-1 crash:
calling `Stop()` while the D3D9 vout is still attached and mid-initialization tears down a half-built
vout and crashes natively. **LESSON: the original "MINIMAL STOP — do NOT call Stop() here" comment
was load-bearing, not stale fear.** Fix #15's `Stop()` is safe only because OnClosed runs it AFTER
detach (detach → settle → Stop). `Stop()` in the live exit path runs before detach = crash. Reverted
to detach-only; full Stop happens in OnClosed as before.

## NEW CRASH TYPE (2026-01-23) - Exit/Disposal Crash
**Different from previous crashes!** Previous crashes were during PLAY (N-th attempt). This crash is during EXIT/DISPOSAL (2nd cycle).

### Log: console_20260123_190139.log (V0032)
**Configuration**: 1 monitor, 768x576 4:3 video (greenCrystals_15Sec_4x3.mp4)

**Timeline**:
- 19:07:06 - First play loaded successfully
- 19:07:19 - Video started playing (MEDIA PLAYING event)
- 19:07:30 - User pressed Escape, disposal started
- 19:07:30.827 - Line 61: "*** DISPOSAL STEP 6: Disposing MediaPlayer ***"
- **CRASH** - App froze and crashed immediately after this line

**Analysis**:
1. Crash happens INSIDE `_mediaPlayer.Dispose()` at VideoWindow.xaml.cs:758
2. Try-catch around Dispose() didn't catch it → **Native crash in LibVLC DLL**
3. Never reached "DISPOSAL STEP 7: MediaPlayer set to null"
4. Current disposal order may be problematic:
   - Set `MediaPlayer.Media = null` (Step 4, line 746)
   - Dispose `MediaPlayer` (Step 6, line 758) ← CRASH HERE
   - Dispose `_media` object (much later, lines 769-773) ← TOO LATE!

**Root Cause Hypothesis**:
MediaPlayer.Dispose() crashes because the associated Media object hasn't been disposed yet. The Media object is still alive when we try to dispose MediaPlayer, causing LibVLC's native code to crash.

**Fix**: Dispose Media BEFORE MediaPlayer

## Current State
- Audio state fix (#3, #4) moved crash from 2nd to 4th attempt
- Cleanup/GC improvements (#5, #6) moved crash from 4th to 6th attempt
- **Auto-reset feature added (V0013)** - resets LibVLC after N cycles
- **Double-counting bug fixed (V0014)** - now counts correctly
- **RESET IS HARMFUL (V0015)** - lowering threshold causes EARLIER crash!
- **Current Build: V0015** - Crashed on 3rd play (1st after reset)

### CRITICAL FINDING (V0015 - Log console_20260117_194844.log)
**The LibVLC reset is CAUSING earlier crashes, not preventing them!**

V0015 Timeline (threshold=2):
- Play 1: WORKED (2 MEDIA PLAYING events at lines 45-46)
- Play 2: WORKED (2 MEDIA PLAYING events at lines 138-139), AUTO-RESET at line 194
- Play 3: **CRASH** - Only 1 MEDIA PLAYING at line 236 (FIRST play after reset!)

Comparison:
- V0014 (threshold=3): Crash at play 5 (2nd after reset at play 3)
- V0015 (threshold=2): Crash at play 3 (1st after reset at play 2)

**Conclusion**: The reset itself destabilizes LibVLC! More resets = earlier crash.

### Previous Finding (V0014)
With threshold=3, crash happened on 5th play (2nd after reset).
This suggested crash on "2nd use of fresh instance" but V0015 proves that wrong.
The reset itself is the problem.

Double-counting fix confirmed working:
- Lines 106, 199, 297, 390 all show "No windows to close, skipping cleanup"

### Finding (V0013 - Double-counting bug)
- Each user play was incrementing cycle count by 2
- ExitRequested handler calls CloseAllWindows (cycle +1)
- Then MainViewModel.Stop() calls CloseAllWindows again (cycle +1)
- Fix: Only increment cycle count if windows were actually closed

### V0014 Changes
1. Fixed double-counting: CloseAllWindows now checks if there were windows before counting
2. Reduced default threshold from 4 to 3 (more conservative)
3. With proper counting: Play 1 = cycle 1, Play 2 = cycle 2, Play 3 = cycle 3 (reset)

### V0015 Plan
Since crash happens on 2nd use of any LibVLC instance, try:
- Reduce threshold to 2 (reset after every 2 plays, before crash point)

## Code Changes Summary

### VideoWindow.xaml.cs
```csharp
// After creating MediaPlayer - RESET AUDIO STATE
_mediaPlayer = new MediaPlayer(libVLC);
_mediaPlayer.Mute = false;  // NEW
_mediaPlayer.Volume = 100;   // NEW

// On shutdown - STOP instead of MUTE
// OLD: _mediaPlayer.Mute = true; _mediaPlayer.Volume = 0;
// NEW: _mediaPlayer.Stop();

// After Dispose - DELAY
_mediaPlayer.Dispose();
_mediaPlayer = null;
Thread.Sleep(50);  // NEW
```

### WindowManager.cs
```csharp
// CloseAllWindows - LONGER DELAY + GC
Thread.Sleep(500);  // Was 300ms
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
```

## Next Steps if Still Crashing
1. Check if crash count increased (4th -> higher = progress)
2. Look for resource leaks in LibVLC (track MediaPlayer count)
3. Consider creating fresh LibVLC instance periodically
4. Add try-catch around sync timer operations
5. Check thread safety of _activeWindows dictionary access

### V0043 Test Results (console_20260123_202727.log)
**SUCCESS!** Disposal crash is FIXED.

Timeline:
- **Cycle 1** (lines 14-87): Load → Play → Escape → Disposal SUCCESS
- **Cycle 2** (lines 88-152): Load → Play → Escape → Disposal SUCCESS

Both cycles show:
- Line 64/129: "*** DISPOSAL STEP 4: MediaPlayer stopped ***"
- Line 72/137: "*** DISPOSAL STEP 10: MediaPlayer disposed successfully ***"

**Root cause**: MediaPlayer needed explicit Stop() call before disposal.

**Note**: User reported visual rendering issue on 2nd play (white artifacts instead of black pillarboxing), but NO CRASH occurred.

## 2026-06-10 — VERIFIED ROOT CAUSE: Sync loop driving a non-Playing player (V0082, Fix #16)

**Crash type**: Native access violation mid-sync on ~5th perform cycle, video-only, 2 monitors.
**Disposal is EXONERATED for this crash**: both log writers run with AutoFlush=true, so a missing
log line means the code never ran. On the crash cycle the log ends mid-sync, BEFORE any Escape /
STOP / DISPOSAL lines — the process died while the sync timer was running, not during teardown.

### Root cause 1 — Trigger (the AV itself)
`_readyForSync` was armed by a blind 300ms `Task.Delay` after MediaOpened (VideoWindow.xaml.cs,
`TryFireMediaOpened`), NOT gated on the Playing state. On the crash cycle, window 2 logged
"WINDOW READY FOR SYNC" but never "MEDIA PLAYING" — it was stuck in Opening. `OnSyncTimerTick`
then read Position, called SetSpeedRatio, and at >500ms drift called `Seek()` on that stuck
player. Calling set_time/set_rate on a player whose input/vout thread is still spinning up is a
native access violation.

### Root cause 2 — Accumulation (why it became reliable by cycle ~5, always window 2)
`VideoWindow.Stop()` only detached the view (`VideoPlayer.MediaPlayer = null`) WITHOUT stopping
playback. In the multi-window close path, window 2 was left PLAYING into a null hwnd while
window 1's OnClosed blocked the UI thread in ~700ms of disposal sleeps. This leaked D3D9/DXVA2
decoder state in the shared LibVLC instance every cycle, until the second concurrent decoder
open wedged in Opening — feeding root cause 1.

### Fixes applied (V0082)
1. **Gate sync on actual Playing state** (VideoWindow.xaml.cs):
   - `_readyForSync` is now set in `OnMediaPlaying` (the player has genuinely reached Playing),
     and the blind 300ms arming path was deleted.
   - `IsReadyForSync` now also live-checks `MediaPlayer.State == VLCState.Playing`, so the sync
     loop can never Seek/SetSpeedRatio a non-Playing player (also skips paused players).
2. **Stop before detach; stop-all before disposing** (VideoWindow.xaml.cs, WindowManager.cs):
   - `VideoWindow.Stop()` now calls `MediaPlayer.Stop()` BEFORE detaching the view — no player
     is ever left decoding into a detached hwnd.
   - `CloseAllWindows()` now calls `StopAll()` as its first step, so ALL players are stopped
     before ANY window's blocking OnClosed disposal runs (covers every exit path, including the
     video-wall pre-create cleanup which previously closed without stopping).

The Stop()-before-Dispose() disposal sequence in OnClosed (Fix #15) is unchanged and remains
required. No delays were added or increased (LESSON-BUG-001). LibVLCProvider untouched.

**Verification**: build green (0 warnings). Awaiting manual 10-cycle perform-mode test on
target hardware.

## Log Files for This Session
- console_20260117_174851.log - Crashed on 4th try (after fixes 1-4)
- console_20260117_185215.log - V0013 test: auto-reset worked at cycle 4, but crash at 4th play (double-counting issue)
- console_20260117_190207.log - V0014 test: double-counting fixed, crash at 5th play (2nd after reset)
- console_20260117_194844.log - V0015 test: threshold=2 WORSE, crash at 3rd play (1st after reset!)
- Awaiting V0016 test log (auto-reset disabled, longer cleanup)
