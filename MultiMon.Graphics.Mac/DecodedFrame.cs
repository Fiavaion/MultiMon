using Metal;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// One decoded video frame handed from a decode thread to the render thread — immutable once published.
/// It carries a GPU texture on the shared device (a HAP upload, or a VideoToolbox CVMetalTexture) plus its
/// presentation timestamp. The render thread copies the payload into the pass's persistent source texture
/// and the <see cref="FrameTimeline"/> disposes the frame once it has been passed, which runs the
/// producer-supplied release exactly once (returning the texture to the decoder's pool / dropping the
/// CVPixelBuffer). Exactly one holder at a time; <see cref="Dispose"/> is idempotent.
///
/// GPU fence: the pass's copy is ENCODED when the timeline disposes the frame, not executed — Metal runs it
/// later, so the texture must not be recycled on <see cref="Dispose"/> alone. The frame therefore counts
/// holds: one for its owner (dropped by Dispose) plus one per encoded read, registered by the committer through
/// <see cref="HoldUntilCompleted"/> and dropped by that command buffer's completed handler. The release
/// closure runs when the LAST hold drops, whichever side that is — so a pooled texture is reusable only
/// after every command buffer that read it has completed. A frame that was never copied releases on Dispose.
/// </summary>
public sealed class DecodedFrame : IDisposable
{
    private Action? _release;
    private int _holds = 1;  // the owner's hold (+1 per in-flight GPU read)
    private int _disposed;

    /// <summary>The copy source on the shared device. Borrowed — freed by the release closure.</summary>
    public IMTLTexture Texture { get; }

    public TimeSpan Pts { get; }

    public DecodedFrame(IMTLTexture texture, TimeSpan pts, Action release)
    {
        Texture = texture;
        Pts = pts;
        _release = release;
    }

    /// <summary>
    /// Render thread, from the scope that COMMITS <paramref name="commandBuffer"/> (which has a read of
    /// <see cref="Texture"/> encoded by the pass), immediately before Commit: keeps the texture held until that
    /// command buffer has completed. Taken by the committer, never the encoder, so a buffer abandoned before
    /// Commit (whose completed handler never fires) cannot strand the hold.
    /// </summary>
    internal void HoldUntilCompleted(IMTLCommandBuffer commandBuffer)
    {
        Interlocked.Increment(ref _holds);
        commandBuffer.AddCompletedHandler(OnReadCompleted);
    }

    /// <summary>Metal completion thread. The binding hands over a fresh retained wrapper per callback — released
    /// here so a retained command buffer never pins its drawable until a GC pass (see OutputWindow).</summary>
    private void OnReadCompleted(IMTLCommandBuffer commandBuffer)
    {
        commandBuffer.Dispose();
        DropHold();
    }

    private void DropHold()
    {
        if (Interlocked.Decrement(ref _holds) == 0)
            Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            DropHold();
    }
}
