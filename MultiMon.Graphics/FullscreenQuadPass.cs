using System.Runtime.InteropServices;
using MultiMon.Core.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MultiMon.Graphics;

/// <summary>
/// The one fullscreen pass: a vertex-ID-generated fullscreen triangle that draws EITHER the
/// Milestone 1 animated test pattern (no source bound) OR a decoded source texture sampled with UV
/// passthrough (Milestone 2). Shaders, constant buffers, rasterizer state and sampler are NOT owned
/// here — they are the device-wide <see cref="QuadPipeline"/> built once per device by
/// <see cref="GraphicsDeviceProvider"/>. A pass owns only its persistent source texture + SRV
/// (created once in <see cref="BindSource"/>, sized to the video); nothing is built per cycle
/// (LESSON-ARCH-002), and constructing a pass creates no device objects at all.
///
/// Threading: the constructor, <see cref="BindSource"/>, and <see cref="Dispose"/> only touch the
/// device (free-threaded) and are safe off the render thread; <see cref="Draw"/> touches the
/// immediate context (including the per-frame copy of the selected decoded frame into the source
/// texture) and is called EXCLUSIVELY by the render thread.
/// </summary>
public sealed class FullscreenQuadPass : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Size = 16)] // cbuffer slots are 16-byte aligned
    private struct TimeConstants
    {
        public float Time;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)] // float4 gUvRect
    private struct UvConstants
    {
        public float U0;
        public float V0;
        public float U1;
        public float V1;
    }

    // The device is mutable: device-removed recovery (RecreateDeviceResources) re-points it at the new
    // device before rebuilding the source texture.
    private ID3D11Device _device;

    // Source-sampling state (M2 + M3 + M5). _source/dims/format persist across a device-removed recreate
    // (the FrameTimeline is owned by the source and is re-armed, not replaced); only the GPU texture+view
    // are device-bound and rebuilt. Null until BindSource; then persistent for the session.
    private FrameTimeline? _source;
    private int _sourceWidth;
    private int _sourceHeight;
    private Format _sourceFormat = Format.B8G8R8X8_UNorm; // MF RGB32 default; HAP sets a BCn format
    private bool _useYCoCg;                                // HAP Q (YCoCg-DXT5) needs the conversion shader
    private ID3D11Texture2D? _sourceTexture;
    private ID3D11ShaderResourceView? _sourceView;
    private bool _hasContent; // a frame has been copied into _sourceTexture at least once

    // The frame most recently copied into _sourceTexture, by IDENTITY only — never dereferenced. A pass
    // shared by N outputs (Span/Split) is drawn N times per loop iteration against the same clock sample,
    // and the same frame is re-selected on every iteration until the clock passes it; without this the
    // full frame was re-copied on every Draw (N × per-iteration for 4K sources). Frames are immutable once
    // published and DecodedFrame objects are never reused, so "same reference" ⇒ "same pixels already in
    // the texture". Cleared whenever the texture is (re)built.
    private DecodedFrame? _lastCopied;

    /// <summary>Creates a pass. Creates NO device objects: the shared pipeline is the provider's, and the
    /// source texture is built by <see cref="BindSource"/>. <paramref name="device"/> is where that texture
    /// (and the format-support check) will go.</summary>
    public FullscreenQuadPass(ID3D11Device device)
    {
        _device = device;
    }

    /// <summary>
    /// Binds the decode source this pass samples and creates the persistent shader-resource texture
    /// (sized to the video) it copies each frame into. <paramref name="textureFormat"/> is the source's
    /// upload format (MF RGB32 → B8G8R8X8_UNorm; HAP → a BCn format) and <paramref name="useYCoCg"/>
    /// selects the HapQ YCoCg→RGB shader. Called ONCE, off the render thread, before the pass is shown —
    /// device resource creation is free-threaded. Idempotent guard: a second bind is rejected so the
    /// persistent texture is never rebuilt. A throwing bind (e.g. unsupported format) leaves the pass
    /// unbound with no device objects, so the caller may retry with another format/source.
    /// </summary>
    public void BindSource(FrameTimeline source, int width, int height,
        Format textureFormat = Format.B8G8R8X8_UNorm, bool useYCoCg = false)
    {
        if (_source is not null)
            throw new InvalidOperationException("FullscreenQuadPass already has a bound source.");

        _sourceWidth = Math.Max(1, width);
        _sourceHeight = Math.Max(1, height);
        _sourceFormat = textureFormat;
        _useYCoCg = useYCoCg;
        CreateSourceTexture();
        _source = source; // publish last: Draw keys off _source being non-null
    }

    /// <summary>
    /// Creates the persistent shader-resource texture (sized to the video) on <see cref="_device"/> in
    /// the bound source format. B8G8R8X8 = Media Foundation's RGB32 output; HAP uses a BCn format
    /// (e.g. BC3 for Hap/HapQ) whose CPU bytes the decode thread uploads directly. The copy source and
    /// dest MUST share a DXGI typeless family or CopySubresourceRegion silently no-ops, so the MF path's
    /// B8G8R8X8 dest matches its BGRX frames; the HAP path uploads compressed bytes via UpdateSubresource.
    /// On failure nothing is left half-built: any texture created before the SRV failed is released.
    /// </summary>
    private void CreateSourceTexture()
    {
        // Validate the format against the GPU before creating the texture, so an unsupported format (e.g. a
        // BCn HAP format on a weak GPU that slipped past the build-time gate) fails with a legible message
        // the caller can catch and route to a fallback/skip — not an opaque native error (cross-GPU, G1/G3).
        if ((_device.CheckFormatSupport(_sourceFormat) & FormatSupport.Texture2D) == 0)
            throw new NotSupportedException($"GPU does not support texture format {_sourceFormat}.");

        var description = new Texture2DDescription
        {
            Width = (uint)_sourceWidth,
            Height = (uint)_sourceHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = _sourceFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };

        ReleaseSourceTexture(); // never overwrite a live texture/view (a failed earlier bind, or recreate)
        var texture = _device.CreateTexture2D(description);
        try
        {
            _sourceView = _device.CreateShaderResourceView(texture);
        }
        catch
        {
            texture.Dispose();
            throw;
        }
        _sourceTexture = texture;
    }

    private void ReleaseSourceTexture()
    {
        _sourceView?.Dispose();
        _sourceView = null;
        _sourceTexture?.Dispose();
        _sourceTexture = null;
        _hasContent = false;   // a new/absent texture holds no frame
        _lastCopied = null;
    }

    /// <summary>
    /// Device-removed recovery, render thread only. Releases the pass's device-bound GPU objects (the
    /// source texture+view; the shared pipeline is the provider's and is released with the device graph).
    /// The <see cref="_source"/> binding and its dimensions are kept so <see cref="RecreateDeviceResources"/>
    /// can rebuild on the new device.
    /// </summary>
    internal void ReleaseDeviceResources() => ReleaseSourceTexture();

    /// <summary>
    /// Device-removed recovery, render thread only. Rebuilds the pass's GPU resources on the provider's
    /// NEW device. Called AFTER <see cref="GraphicsDeviceProvider.Recreate"/> and the matching
    /// <see cref="ReleaseDeviceResources"/>.
    /// </summary>
    internal void RecreateDeviceResources(ID3D11Device device)
    {
        _device = device;
        if (_source is not null)
            CreateSourceTexture();
    }

    /// <summary>
    /// Renders into <paramref name="renderTarget"/> using the device's shared <paramref name="pipeline"/>.
    /// Render thread ONLY. <paramref name="mediaTime"/> is the MasterClock time: the source path selects
    /// the frame matching it (so all outputs sharing this pass show the same frame); the test-pattern path
    /// animates by its seconds.
    /// </summary>
    internal void Draw(ID3D11DeviceContext context, QuadPipeline pipeline, ID3D11RenderTargetView renderTarget,
        int width, int height, TimeSpan mediaTime, UvRect uv)
    {
        if (_source is not null)
        {
            DrawSource(context, pipeline, renderTarget, width, height, mediaTime, uv);
            return;
        }

        var constants = new TimeConstants { Time = (float)mediaTime.TotalSeconds };
        context.UpdateSubresource(constants, pipeline.TimeBuffer);

        context.OMSetRenderTargets(renderTarget);
        context.RSSetViewport(0, 0, width, height);
        context.RSSetState(pipeline.RasterizerState);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(pipeline.VertexShader);
        context.PSSetShader(pipeline.PatternShader);
        context.PSSetConstantBuffer(0, pipeline.TimeBuffer);
        context.Draw(3, 0);
    }

    private void DrawSource(ID3D11DeviceContext context, QuadPipeline pipeline, ID3D11RenderTargetView renderTarget,
        int width, int height, TimeSpan mediaTime, UvRect uv)
    {
        // Unbind the source view before the copy so the texture is never a copy dest and an SRV at
        // once (a debug-layer hazard); rebind it for the draw below. null marshals to a null SRV =
        // "clear slot 0" (Vortice's parameter is non-nullable, hence null!).
        context.PSSetShaderResource(0, null!);

        // Select the frame for the MasterClock time and copy it into the persistent texture — unless it
        // is the frame already there (see _lastCopied); passed frames are pruned inside SelectInto. The
        // copy runs on the render thread (immediate context). Lifetime: SelectInto hands us the frame
        // while it is still buffered in the timeline (only PASSED frames are disposed, after the callback
        // returns), so the copy source is alive for the whole copy; we keep only its reference afterwards.
        if (_source!.SelectInto(mediaTime, frame => CopyFrameIfNew(context, frame)))
            _hasContent = true;

        context.OMSetRenderTargets(renderTarget);
        context.RSSetViewport(0, 0, width, height);

        if (!_hasContent)
        {
            // No decoded frame yet — present black rather than stale/garbage memory (still no wedge).
            context.ClearRenderTargetView(renderTarget, new Color4(0f, 0f, 0f, 1f));
            return;
        }

        // This output's UV sub-rect (M7). Written per Draw because the pass is shared across outputs in
        // spanning/quad-split — each output samples a different slice of the one source texture.
        var uvConstants = new UvConstants { U0 = uv.U0, V0 = uv.V0, U1 = uv.U1, V1 = uv.V1 };
        context.UpdateSubresource(uvConstants, pipeline.UvBuffer);

        context.RSSetState(pipeline.RasterizerState);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(pipeline.VertexShader);
        context.PSSetShader(_useYCoCg ? pipeline.YCoCgShader : pipeline.SampleShader); // HapQ needs YCoCg→RGB; others passthrough
        context.PSSetConstantBuffer(1, pipeline.UvBuffer);
        context.PSSetShaderResource(0, _sourceView!); // non-null whenever _source is bound (_hasContent)
        context.PSSetSampler(0, pipeline.Sampler);
        context.Draw(3, 0);
    }

    /// <summary>Copies one decoded frame into the persistent source texture unless it is already the frame
    /// there. Render thread (immediate context).</summary>
    private void CopyFrameIfNew(ID3D11DeviceContext context, DecodedFrame frame)
    {
        if (ReferenceEquals(frame, _lastCopied))
            return;
        if (frame.Texture is not null)
            context.CopySubresourceRegion(_sourceTexture!, 0, 0, 0, 0, frame.Texture, frame.Subresource, null);
        else
            context.UpdateSubresource<byte>(frame.Pixels.Span, _sourceTexture!, 0, (uint)frame.RowPitch, 0, null);
        _lastCopied = frame;
    }

    public void Dispose() => ReleaseSourceTexture();
}
