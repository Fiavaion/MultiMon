using Vortice.Direct3D11;

namespace MultiMon.Graphics;

/// <summary>
/// One decoded video frame handed from a decode thread to the render thread — immutable once
/// published. It carries EITHER a GPU texture (hardware decode: the decoder/video-processor output
/// on our shared device) OR a CPU pixel buffer (software-decode fallback), plus its presentation
/// timestamp. The render thread copies the payload into a persistent shader-resource texture and
/// then disposes the frame, which releases the underlying native objects via <see cref="_release"/>.
///
/// Ownership: exactly one holder at a time (the <see cref="TripleBuffer"/> mailbox OR the consumer
/// mid-copy). <see cref="Dispose"/> is idempotent and runs the producer-supplied release exactly
/// once, so a frame that is superseded before it is consumed is still freed (no leaked MF samples).
/// Graphics stays free of any Media Foundation type: the producer captures the MF sample/buffer in
/// the <paramref name="release"/> closure.
/// </summary>
public sealed class DecodedFrame : IDisposable
{
    private Action? _release;

    /// <summary>Hardware path: the copy source on our shared device. Borrowed — freed by <see cref="_release"/>.</summary>
    public ID3D11Texture2D? Texture { get; }

    /// <summary>Array slice of <see cref="Texture"/> holding this frame (decoder pool textures are arrays).</summary>
    public uint Subresource { get; }

    /// <summary>Software path: tightly-packed BGRA pixels (null when <see cref="Texture"/> is set).</summary>
    public ReadOnlyMemory<byte> Pixels { get; }

    /// <summary>Row pitch in bytes for <see cref="Pixels"/>.</summary>
    public int RowPitch { get; }

    public TimeSpan Pts { get; }

    /// <summary>Hardware frame: a GPU texture on the shared device.</summary>
    public DecodedFrame(ID3D11Texture2D texture, uint subresource, TimeSpan pts, Action release)
    {
        Texture = texture;
        Subresource = subresource;
        Pts = pts;
        _release = release;
    }

    /// <summary>Software-fallback frame: a CPU BGRA buffer.</summary>
    public DecodedFrame(ReadOnlyMemory<byte> pixels, int rowPitch, TimeSpan pts, Action release)
    {
        Pixels = pixels;
        RowPitch = rowPitch;
        Pts = pts;
        _release = release;
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
