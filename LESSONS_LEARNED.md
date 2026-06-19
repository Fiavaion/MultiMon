# Lessons Learned: LibVLC Multi-Instance Crash Bug

**Date:** 2025-01-13
**Duration:** ~3 days of debugging
**Severity:** Critical (app crash)

---

## The Problem

Application crashed after multiple play/escape cycles when audio tracks were loaded. Initially crashed on 4th cycle, improved to 10th cycle with partial fixes, finally resolved with unified LibVLC provider.

### Symptoms
- Hard crash (process termination, no exception logged)
- Crash occurred during `MediaPlayer.Stop()` call
- Only happened when audio-only tracks were added to project
- Worked fine without audio tracks (6+ cycles)
- Got progressively worse over cycles (accumulated corruption)

---

## Root Causes Identified

### 1. WASAPI Conflict (NAudio + LibVLC)
**Problem:** NAudio (WasapiOut) and LibVLC both use Windows Audio Session API (WASAPI). Having two audio libraries fighting over the same audio subsystem caused resource conflicts.

**Symptom:** Crash on 4th cycle.

**Solution:** Replace NAudio with LibVLC for audio tracks. Single audio backend = no conflicts.

### 2. Dual LibVLC Instances
**Problem:** After switching to LibVLC for audio, we had TWO separate static LibVLC instances:
- `VideoWindow._sharedLibVLC` - disposed when all windows close
- `AudioTrackManager._sharedLibVLC` - never disposed during session (tracks just stopped, not removed)

Both called `Core.Initialize()` separately. When VideoWindow disposed its instance but AudioTrackManager's stayed alive, the native libvlc library got into a corrupted state over multiple cycles.

**Symptom:** Crash on 10th cycle.

**Solution:** Single `LibVLCProvider` with reference counting. One `Core.Initialize()` call, one LibVLC instance for the entire app.

---

## What Didn't Work

| Attempt | Approach | Why It Failed |
|---------|----------|---------------|
| Increased delays | 500ms → 2000ms cleanup delay | Timing wasn't the issue - resource corruption was |
| App restart on Escape | Fresh process each cycle | Still crashed - the crash happened before cleanup completed |
| Gentler Stop() | Pause before Stop, check state | Masked symptoms but didn't fix root cause |
| Skip volume/mute in Stop | Avoid operations that crashed | Just moved crash to different operation |

### Key Pattern: Surface-Level Fixes
Many early fixes targeted **symptoms** rather than **causes**:
- "Crash during volume set" → Skip volume set → Crash during Stop
- "Crash during Stop" → Add delay → Still crashes, just later
- "Resources not cleaned" → Restart app → Still crashes

---

## What Worked

### 1. Eliminating Competing Audio Libraries
**Before:** NAudio (WASAPI) + LibVLC (WASAPI) = conflict
**After:** LibVLC only = no conflict

**Lesson:** Don't mix audio libraries that use the same underlying API.

### 2. Unified Resource Provider Pattern
**Before:**
```
VideoWindow: static _sharedLibVLC + Core.Initialize()
AudioTrackManager: static _sharedLibVLC + Core.Initialize()
```

**After:**
```
LibVLCProvider: static _instance + Core.Initialize() (once only)
├── VideoWindow: Acquire() / Release()
└── AudioTrackManager: Acquire() / Release()
```

**Lesson:** Shared native resources should have a single owner with reference counting.

---

## Key Takeaways

### 1. Native Libraries Need Centralized Management
When using native libraries (LibVLC, FFmpeg, etc.):
- Have ONE initialization point
- Use reference counting for shared instances
- Don't dispose until ALL users are done
- Never call Initialize() multiple times

### 2. Symptoms vs Causes
When debugging:
- "Crash during X operation" doesn't mean X is broken
- Look for resource leaks, race conditions, state corruption
- Ask "what changed between working and not working?"

### 3. Logs Tell the Story
The breakthrough came from analyzing logs:
- "10 successful cycles, crash on 10th" → accumulated issue
- "STOP: Calling MediaPlayer.Stop()..." with no "STOP COMPLETE" → crash location
- Counting ESCAPE events revealed pattern

### 4. Don't Abandon Ideas Too Quickly
The restart approach was abandoned after one failed attempt. Sometimes ideas need refinement, not rejection.

### 5. Test with Full Feature Set
The bug only appeared with audio tracks. Testing without the full feature set hid the problem.

---

## Architecture Pattern: Native Resource Provider

```csharp
public static class NativeResourceProvider
{
    private static NativeLib? _instance;
    private static int _refCount;
    private static readonly object _lock = new();
    private static bool _initialized;

    public static NativeLib Acquire()
    {
        lock (_lock)
        {
            if (_instance == null)
            {
                if (!_initialized)
                {
                    NativeLib.GlobalInit(); // Only once per process!
                    _initialized = true;
                }
                _instance = new NativeLib();
            }
            _refCount++;
            return _instance;
        }
    }

    public static void Release()
    {
        lock (_lock)
        {
            _refCount--;
            if (_refCount <= 0 && _instance != null)
            {
                _instance.Dispose();
                _instance = null;
                _refCount = 0;
            }
        }
    }
}
```

---

## Red Flags to Watch For

1. **Multiple static instances of native libraries** - Centralize immediately
2. **Calling Initialize() in multiple places** - Should be exactly once
3. **Different disposal lifecycles for shared resources** - Use reference counting
4. **Mixing libraries for the same purpose** (NAudio + LibVLC for audio) - Pick one
5. **"Works X times then crashes"** - Accumulated corruption, look for leaks
6. **Hard crashes with no exception** - Native code issue, check resource management

---

## Files Changed in Final Fix

| File | Change |
|------|--------|
| `Services/Media/LibVLCProvider.cs` | NEW - Centralized LibVLC management |
| `Views/VideoWindow.xaml.cs` | Removed local LibVLC management, uses provider |
| `Services/Audio/AudioTrackManager.cs` | Removed local LibVLC management, uses provider |

---

## Testing Checklist for Future Native Resource Issues

- [ ] Test feature in isolation
- [ ] Test feature combined with all other features
- [ ] Run 10+ cycles of operation
- [ ] Check for resource leaks (memory, handles)
- [ ] Verify single initialization of native libs
- [ ] Confirm proper cleanup order
- [ ] Test with logging enabled to capture crash point
