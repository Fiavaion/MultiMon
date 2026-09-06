using System.Runtime.InteropServices;
using Metal;
using MultiMon.Core.Models;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// The one fullscreen pass: a vertex-id-generated fullscreen triangle that draws EITHER the animated test
/// pattern (no source bound) OR a decoded source texture sampled through the output's UV sub-rect. Pipeline
/// states and the sampler are the device-wide <see cref="QuadPipeline"/>; a pass owns only its persistent
/// source texture (created once in <see cref="BindSource"/>, sized to the video). Constructing a pass creates
/// no device objects at all; nothing is built per cycle (LESSON-ARCH-002).
///
/// Threading: the constructor, <see cref="BindSource"/> and <see cref="Dispose"/> touch only the device
/// (free-threaded) and are safe off the render thread; <see cref="Draw"/> encodes into the output's command
/// buffer (including the blit of the selected decoded frame into the source texture) and is called
/// EXCLUSIVELY by the render thread.
/// </summary>
public sealed class FullscreenQuadPass : IDisposable
{
    /// <summary>
    /// Byte-exact mirror of the MSL <c>QuadUniforms</c> block in Quad.metal: time@0, three scalar pads@4..12,
    /// uvRect@16, sizeof 32. MSL alignment rule: float3/float4 are 16-byte aligned, so the shader side must pad
    /// with scalars, never a vector type — a float3 pad there would move uvRect to offset 32 and this struct
    /// would feed it garbage. Field order here is the layout; do not reorder.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = UniformSize)]
    private struct Uniforms
    {
        public float Time;                 // offset 0
        public float Pad0, Pad1, Pad2;     // offsets 4, 8, 12
        public float U0, V0, U1, V1;       // offset 16: uvRect.xyzw
    }

    /// <summary>Bytes of one uniform block; ring slots in the output's uniform buffer are this far apart
    /// (Metal constant-buffer offsets must be 256-byte aligned on macOS, hence the slot stride).</summary>
    public const int UniformSize = 32;
    public const int UniformSlotStride = 256;

    private readonly GraphicsDeviceProvider _provider;

    private FrameTimeline? _source;
    private int _sourceWidth;
    private int _sourceHeight;
    private bool _useYCoCg;
    private IMTLTexture? _sourceTexture;
    private bool _hasContent;   // a frame has been copied into _sourceTexture at least once
    private DecodedFrame? _lastCopied; // identity only, never dereferenced (see the Windows pass)

    public FullscreenQuadPass(GraphicsDeviceProvider provider)
    {
        _provider = provider;
    }

    /// <summary>
    /// Binds the decode source this pass samples and creates the persistent source texture (sized to the
    /// video) it copies each frame into. <paramref name="format"/> is the decoder's upload format (BGRA8 for
    /// VideoToolbox; a BCn format for HAP — the persistent texture is created in that SAME format, the blit in
    /// Draw is format-to-format, and the sampler decompresses BCn on read) and <paramref name="useYCoCg"/>
    /// selects the HapQ pipeline. Called
    /// ONCE, off the render thread, before the pass is shown. A second bind is rejected so the persistent
    /// texture is never rebuilt; a throwing bind leaves the pass unbound with no device objects.
    /// </summary>
    public void BindSource(FrameTimeline source, int width, int height, MTLPixelFormat format, bool useYCoCg = false)
    {
        if (_source is not null)
            throw new InvalidOperationException("FullscreenQuadPass already has a bound source.");

        _sourceWidth = Math.Max(1, width);
        _sourceHeight = Math.Max(1, height);
        _useYCoCg = useYCoCg;

        using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(format, (nuint)_sourceWidth, (nuint)_sourceHeight, false);
        descriptor.Usage = MTLTextureUsage.ShaderRead;
        descriptor.StorageMode = MTLStorageMode.Private;
        _sourceTexture = _provider.Device.CreateTexture(descriptor)
            ?? throw new NotSupportedException($"Metal texture creation failed for {format} {_sourceWidth}x{_sourceHeight}.");
        _provider.Tracker.TextureCreated();
        _source = source; // publish last: Draw keys off _source being non-null
    }

    /// <summary>
    /// Render thread ONLY. Encodes this output's frame into <paramref name="commandBuffer"/>: the source path
    /// selects the frame matching <paramref name="mediaTime"/>, blits it into the persistent texture and draws
    /// it through <paramref name="uv"/>; the pattern path animates by the media seconds. The uniform block is
    /// written into the output's ring slot at <paramref name="uniformBuffer"/>+<paramref name="uniformOffset"/>
    /// (the output guarantees that slot's previous reader has completed).
    /// Returns the decoded frame whose read this command buffer now encodes, or null. INVARIANT: the caller
    /// takes that frame's <see cref="DecodedFrame.HoldUntilCompleted"/> in the same scope that commits,
    /// immediately before Commit, so a command buffer abandoned before Commit never strands a hold.
    /// </summary>
    internal DecodedFrame? Draw(IMTLCommandBuffer commandBuffer, MTLRenderPassDescriptor renderPass, IMTLBuffer uniformBuffer,
        int uniformOffset, int width, int height, TimeSpan mediaTime, UvRect uv)
    {
        var pipeline = _provider.QuadPipeline;
        var hasSource = _source is not null;
        DecodedFrame? read = null;
        if (hasSource)
        {
            // Select the frame for the clock time and blit it into the persistent texture — unless it is the
            // frame already there. Lifetime: the frame is alive for the whole callback (only PASSED frames are
            // disposed, after it returns) and the command buffer retains the source texture until it completes.
            if (_source!.SelectInto(mediaTime, frame => { if (CopyFrameIfNew(commandBuffer, frame)) read = frame; }))
                _hasContent = true;
        }

        var uniforms = new Uniforms
        {
            Time = (float)mediaTime.TotalSeconds,
            U0 = uv.U0, V0 = uv.V0, U1 = uv.U1, V1 = uv.V1
        };
        Marshal.StructureToPtr(uniforms, uniformBuffer.Contents + uniformOffset, false);

        var encoder = commandBuffer.CreateRenderCommandEncoder(renderPass);
        try
        {
            if (hasSource && !_hasContent)
                return null; // no decoded frame yet: the pass's clear leaves black rather than stale memory (no wedge)

            encoder.SetViewport(new MTLViewport(0, 0, width, height, 0, 1));
            encoder.SetFragmentBuffer(uniformBuffer, (nuint)uniformOffset, 0);
            if (hasSource)
            {
                encoder.SetRenderPipelineState(_useYCoCg ? pipeline.YCoCgPipeline : pipeline.SamplePipeline);
                encoder.SetFragmentTexture(_sourceTexture!, 0);
                encoder.SetFragmentSamplerState(pipeline.Sampler, 0);
            }
            else
            {
                encoder.SetRenderPipelineState(pipeline.PatternPipeline);
            }
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3);
        }
        finally
        {
            encoder.EndEncoding();
            encoder.Dispose();
        }
        return read;
    }

    /// <summary>
    /// Blits one decoded frame into the persistent source texture unless it is already there; returns true when
    /// a copy was encoded. The copy is only ENCODED here; the committer holds the frame
    /// (<see cref="DecodedFrame.HoldUntilCompleted"/>) until this command buffer completes, so the timeline
    /// disposing the frame right after this returns cannot recycle a texture the GPU has not read yet. The
    /// frame's format must equal the bound format: a BCn source blits BCn→BCn whole-texture (block-aligned by
    /// construction), BGRA→BGRA for VideoToolbox.
    /// </summary>
    private bool CopyFrameIfNew(IMTLCommandBuffer commandBuffer, DecodedFrame frame)
    {
        if (ReferenceEquals(frame, _lastCopied))
            return false;
        var blit = commandBuffer.BlitCommandEncoder
            ?? throw new InvalidOperationException("Metal blit encoder creation failed.");
        try
        {
            blit.CopyFromTexture(frame.Texture, 0, 0, new MTLOrigin(0, 0, 0),
                new MTLSize(_sourceWidth, _sourceHeight, 1), _sourceTexture!, 0, 0, new MTLOrigin(0, 0, 0));
        }
        finally
        {
            blit.EndEncoding();
            blit.Dispose();
        }
        _lastCopied = frame;
        return true;
    }

    /// <summary>Releases the source texture. PRECONDITION: the render loop is stopped (no command buffer may
    /// still be encoding against it) — the teardown order enforces this.</summary>
    public void Dispose()
    {
        if (_sourceTexture is null) return;
        _sourceTexture.Dispose();
        _sourceTexture = null;
        _provider.Tracker.TextureDisposed();
        _hasContent = false;
        _lastCopied = null;
    }
}
