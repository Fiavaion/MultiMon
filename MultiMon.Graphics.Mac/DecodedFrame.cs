using Metal;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// One decoded video frame handed from a decode thread to the render thread — immutable once published.
/// It carries a GPU texture on the shared device (a HAP upload, or a VideoToolbox CVMetalTexture) plus its
/// presentation timestamp. The render thread copies the payload into the pass's persistent source texture
/// and the <see cref="FrameTimeline"/> disposes the frame once it has been passed, which runs the
/// producer-supplied release exactly once (returning the texture to the decoder's pool / dropping the
/// CVPixelBuffer). Exactly one holder at a time; <see cref="Dispose"/> is idempotent.
/// </summary>
public sealed class DecodedFrame : IDisposable
{
    private Action? _release;

    /// <summary>The copy source on the shared device. Borrowed — freed by the release closure.</summary>
    public IMTLTexture Texture { get; }

    public TimeSpan Pts { get; }

    public DecodedFrame(IMTLTexture texture, TimeSpan pts, Action release)
    {
        Texture = texture;
        Pts = pts;
        _release = release;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
