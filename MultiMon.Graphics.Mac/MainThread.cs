using CoreFoundation;
using Foundation;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// Bounded marshalling onto the AppKit main thread for the window operations that MUST run there
/// (NSWindow create / orderFront / orderOut / close). The caller is the controller or harness thread —
/// NEVER the render thread, which must not block on the main thread (the V0087 deadlock rule; enforced by
/// <see cref="RenderLoop"/> refusing to run these). The wait is bounded so a stuck main thread surfaces
/// as a loud <see cref="TimeoutException"/> the harness reports as a wedge, never as a silent hang.
/// Requires the host to keep the main run loop pumping (the harness does; the Control app's UI loop does).
/// </summary>
public static class MainThread
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static void Invoke(Action action, TimeSpan? timeout = null)
    {
        if (NSThread.IsMain)
        {
            action();
            return;
        }

        Exception? error = null;
        // Not disposed here on purpose: a timed-out block may still Set() it later; the GC reclaims it.
        var done = new ManualResetEventSlim(false);
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        if (!done.Wait(timeout ?? DefaultTimeout))
            throw new TimeoutException($"Main-thread operation did not complete within {(timeout ?? DefaultTimeout).TotalSeconds:0}s — the AppKit main thread is not pumping.");
        if (error is not null)
            throw new InvalidOperationException($"Main-thread operation failed: {error.Message}", error);
    }

    public static T Invoke<T>(Func<T> func, TimeSpan? timeout = null)
    {
        T result = default!;
        Invoke(() => { result = func(); }, timeout);
        return result;
    }
}
