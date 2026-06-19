using System.Runtime.InteropServices;
using MultiMon.Core.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MultiMon.Graphics;

/// <summary>
/// The one fullscreen pass: a vertex-ID-generated fullscreen triangle that draws EITHER the
/// Milestone 1 animated test pattern (no source bound) OR a decoded source texture sampled with UV
/// passthrough (Milestone 2). Shaders, constant buffer, rasterizer state, sampler, and — once a
/// source is bound — the persistent shader-resource texture are created ONCE and reused for the whole
/// session; nothing here is built per cycle (LESSON-ARCH-002).
///
/// Threading: the constructor, <see cref="BindSource"/>, and <see cref="Dispose"/> only touch the
/// device (free-threaded) and are safe off the render thread; <see cref="Draw"/> touches the
/// immediate context (including the per-frame copy of the latest decoded frame into the source
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
    // device before rebuilding the GPU objects below. All fields are assigned in BuildDeviceResources.
    private ID3D11Device _device;
    private ID3D11VertexShader _vertexShader = null!;
    private ID3D11PixelShader _patternShader = null!;
    private ID3D11PixelShader _sampleShader = null!;
    private ID3D11PixelShader _ycocgShader = null!;
    private ID3D11Buffer _timeBuffer = null!;
    private ID3D11Buffer _uvBuffer = null!;   // b1: per-output UV sub-rect, written per Draw (M7)
    private ID3D11RasterizerState _rasterizerState = null!;
    private ID3D11SamplerState _sampler = null!;

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

    public FullscreenQuadPass(ID3D11Device device)
    {
        _device = device;
        BuildDeviceResources();
    }

    /// <summary>Compiles the shaders and creates the constant buffer, rasterizer state, and sampler on <see cref="_device"/>.</summary>
    private void BuildDeviceResources()
    {
        var source = LoadShaderSource();
        var vsBytecode = Compile(source, "VSMain", "vs_5_0");
        var patternBytecode = Compile(source, "PSMain", "ps_5_0");
        var sampleBytecode = Compile(source, "PSSample", "ps_5_0");
        var ycocgBytecode = Compile(source, "PSSampleYCoCg", "ps_5_0");

        _vertexShader = _device.CreateVertexShader(vsBytecode);
        _patternShader = _device.CreatePixelShader(patternBytecode);
        _sampleShader = _device.CreatePixelShader(sampleBytecode);
        _ycocgShader = _device.CreatePixelShader(ycocgBytecode);
        _timeBuffer = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
        _uvBuffer = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
        _rasterizerState = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    /// <summary>
    /// Binds the decode source this pass samples and creates the persistent shader-resource texture
    /// (sized to the video) it copies each frame into. <paramref name="textureFormat"/> is the source's
    /// upload format (MF RGB32 → B8G8R8X8_UNorm; HAP → a BCn format) and <paramref name="useYCoCg"/>
    /// selects the HapQ YCoCg→RGB shader. Called ONCE, off the render thread, before the pass is shown —
    /// device resource creation is free-threaded. Idempotent guard: a second bind is rejected so the
    /// persistent texture is never rebuilt.
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
        _sourceTexture = _device.CreateTexture2D(description);
        _sourceView = _device.CreateShaderResourceView(_sourceTexture);
        _hasContent = false; // the new texture is empty until the next frame is copied in
    }

    /// <summary>
    /// Device-removed recovery, render thread only. Releases every device-bound GPU object (shaders,
    /// state, constant buffer, and the source texture+view). The <see cref="_source"/> binding and its
    /// dimensions are kept so <see cref="RecreateDeviceResources"/> can rebuild on the new device.
    /// </summary>
    internal void ReleaseDeviceResources()
    {
        _sourceView?.Dispose();
        _sourceView = null;
        _sourceTexture?.Dispose();
        _sourceTexture = null;
        _sampler?.Dispose();
        _rasterizerState?.Dispose();
        _uvBuffer?.Dispose();
        _timeBuffer?.Dispose();
        _ycocgShader?.Dispose();
        _sampleShader?.Dispose();
        _patternShader?.Dispose();
        _vertexShader?.Dispose();
        _hasContent = false;
    }

    /// <summary>
    /// Device-removed recovery, render thread only. Rebuilds the pass's GPU resources on the provider's
    /// NEW device. Called AFTER <see cref="GraphicsDeviceProvider.Recreate"/> and the matching
    /// <see cref="ReleaseDeviceResources"/>.
    /// </summary>
    internal void RecreateDeviceResources(ID3D11Device device)
    {
        _device = device;
        BuildDeviceResources();
        if (_source is not null)
            CreateSourceTexture();
    }

    /// <summary>
    /// Renders into <paramref name="renderTarget"/>. Render thread ONLY. <paramref name="mediaTime"/>
    /// is the MasterClock time: the source path selects the frame matching it (so all outputs sharing
    /// this pass show the same frame); the test-pattern path animates by its seconds.
    /// </summary>
    internal void Draw(ID3D11DeviceContext context, ID3D11RenderTargetView renderTarget, int width, int height, TimeSpan mediaTime, UvRect uv)
    {
        if (_source is not null)
        {
            DrawSource(context, renderTarget, width, height, mediaTime, uv);
            return;
        }

        var constants = new TimeConstants { Time = (float)mediaTime.TotalSeconds };
        context.UpdateSubresource(constants, _timeBuffer);

        context.OMSetRenderTargets(renderTarget);
        context.RSSetViewport(0, 0, width, height);
        context.RSSetState(_rasterizerState);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_patternShader);
        context.PSSetConstantBuffer(0, _timeBuffer);
        context.Draw(3, 0);
    }

    private void DrawSource(ID3D11DeviceContext context, ID3D11RenderTargetView renderTarget, int width, int height, TimeSpan mediaTime, UvRect uv)
    {
        // Unbind the source view before the copy so the texture is never a copy dest and an SRV at
        // once (a debug-layer hazard); rebind it for the draw below. null marshals to a null SRV =
        // "clear slot 0" (Vortice's parameter is non-nullable, hence null!).
        context.PSSetShaderResource(0, null!);

        // Select the frame for the MasterClock time and copy it into the persistent texture; passed
        // frames are pruned inside SelectInto. The copy runs on the render thread (immediate context).
        if (_source!.SelectInto(mediaTime, frame => CopyFrame(context, frame)))
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
        context.UpdateSubresource(uvConstants, _uvBuffer);

        context.RSSetState(_rasterizerState);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_useYCoCg ? _ycocgShader : _sampleShader); // HapQ needs YCoCg→RGB; others passthrough
        context.PSSetConstantBuffer(1, _uvBuffer);
        context.PSSetShaderResource(0, _sourceView!); // non-null whenever _source is bound (_hasContent)
        context.PSSetSampler(0, _sampler);
        context.Draw(3, 0);
    }

    /// <summary>Copies one decoded frame into the persistent source texture. Render thread (immediate context).</summary>
    private void CopyFrame(ID3D11DeviceContext context, DecodedFrame frame)
    {
        if (frame.Texture is not null)
            context.CopySubresourceRegion(_sourceTexture!, 0, 0, 0, 0, frame.Texture, frame.Subresource, null);
        else
            context.UpdateSubresource<byte>(frame.Pixels.Span, _sourceTexture!, 0, (uint)frame.RowPitch, 0, null);
    }

    public void Dispose() => ReleaseDeviceResources();

    private static string LoadShaderSource()
    {
        const string resourceName = "MultiMon.Graphics.Shaders.Quad.hlsl";
        using var stream = typeof(FullscreenQuadPass).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded shader '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static byte[] Compile(string source, string entryPoint, string profile)
    {
        var result = Compiler.Compile(source, entryPoint, "Quad.hlsl", profile, out var bytecode, out var errors);
        using (errors)
        {
            if (result.Failure)
                throw new InvalidOperationException($"Shader compile failed ({entryPoint}/{profile}): {errors?.AsString()}");
        }
        using (bytecode)
        {
            return bytecode.AsBytes();
        }
    }
}
