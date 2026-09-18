using Metal;
using MultiMon.Graphics.Mac;

namespace MultiMon.Decode.Mac.Hap;

/// <summary>
/// A fixed set of persistent upload textures for ONE source, created once at the clip's size/format and
/// handed out to the decode thread by <see cref="Rent"/>; every texture is counted in the shared
/// <see cref="MetalResourceTracker"/>. Nothing is created per frame (LESSON-ARCH-002).
///
/// Reuse rule: a texture comes back through <see cref="Return"/> ONLY from a <see cref="DecodedFrame"/>'s
/// release closure, which the frame fires after the timeline has passed it AND every command buffer that
/// read it has completed (the frame's hold count) — so a rented texture is never rewritten under an
/// in-flight blit. <see cref="Rent"/> blocks while all textures are out (bounded by the timeline depth plus
/// the in-flight margin the pool was sized with); <see cref="SignalStop"/> releases a blocked renter.
///
/// Threading: Rent on the decode thread; Return on whichever thread drops the frame's last hold (render or
/// Metal completion thread); SignalStop/Dispose on the owner's teardown thread. A short lock guards the
/// free list. Dispose frees the free textures at once and any still-outstanding texture when it returns —
/// the ordering rule (render loop idle before the source is disposed) makes that path a logged anomaly,
/// never a use-after-free.
/// </summary>
internal sealed class TexturePool : IDisposable
{
    private readonly object _gate = new();
    private readonly Stack<IMTLTexture> _free;
    private readonly MetalResourceTracker _tracker;
    private int _outstanding;
    private bool _stopped;
    private bool _disposed;

    public int Count { get; }

    /// <summary>Creates <paramref name="count"/> textures from <paramref name="descriptor"/>; a failure disposes
    /// the ones already created so a throwing constructor owns nothing.</summary>
    public TexturePool(IMTLDevice device, MTLTextureDescriptor descriptor, int count, MetalResourceTracker tracker)
    {
        _tracker = tracker;
        Count = count;
        _free = new Stack<IMTLTexture>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var texture = device.CreateTexture(descriptor)
                    ?? throw new NotSupportedException($"Metal texture creation failed for {descriptor.PixelFormat} {descriptor.Width}x{descriptor.Height}.");
                tracker.TextureCreated();
                _free.Push(texture);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Decode thread: a texture with no pending reader, or null once <see cref="SignalStop"/> ran.</summary>
    public IMTLTexture? Rent()
    {
        lock (_gate)
        {
            while (_free.Count == 0 && !_stopped)
                Monitor.Wait(_gate);
            if (_stopped)
                return null;
            _outstanding++;
            return _free.Pop();
        }
    }

    /// <summary>From a frame's release closure only (the fence has been satisfied).</summary>
    public void Return(IMTLTexture texture)
    {
        lock (_gate)
        {
            _outstanding--;
            if (_disposed)
            {
                Release(texture);
                return;
            }
            _free.Push(texture);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>Wakes a decode thread blocked in <see cref="Rent"/> so it can observe its stop flag.</summary>
    public void SignalStop()
    {
        lock (_gate)
        {
            _stopped = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Textures still rented when this ran (should be zero after the source's frames were disposed).</summary>
    public int Outstanding
    {
        get { lock (_gate) return _outstanding; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopped = true;
            while (_free.Count > 0)
                Release(_free.Pop());
            Monitor.PulseAll(_gate);
        }
    }

    private void Release(IMTLTexture texture)
    {
        texture.Dispose();
        _tracker.TextureDisposed();
    }
}
