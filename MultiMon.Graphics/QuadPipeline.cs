using Vortice.D3DCompiler;
using Vortice.Direct3D11;

namespace MultiMon.Graphics;

/// <summary>
/// The fullscreen-quad shader + pipeline-state set shared by EVERY <see cref="FullscreenQuadPass"/> on a
/// device: vertex shader, the three pixel shaders (test pattern / passthrough sample / HapQ YCoCg), the
/// two constant buffers, rasterizer state and sampler. Built ONCE per device by
/// <see cref="GraphicsDeviceProvider"/> (created with the device, disposed with the device graph, rebuilt
/// on device-removed recreate) — never per pass, never per cycle (LESSON-ARCH-002). Before this existed,
/// every pass construction D3DCompiled four shaders and recreated the state objects, i.e. 4N compiles per
/// perform in per-monitor mode.
///
/// HLSL bytecode is device-independent, so it is compiled once per PROCESS and cached; only the device
/// objects are per device. The constant buffers are written on the render thread immediately before each
/// draw (time / UV sub-rect), so they carry no per-pass state and are safely shared.
///
/// Threading: constructed on whichever thread creates the device (device calls are free-threaded); the
/// objects are read only by the render thread during Draw.
/// </summary>
internal sealed class QuadPipeline : IDisposable
{
    private sealed record Bytecode(byte[] Vertex, byte[] Pattern, byte[] Sample, byte[] YCoCg);

    // Compiled once per process; a compile failure surfaces on the first device creation (and again on
    // any later one — Lazy caches the exception, which is the honest outcome for a broken shader).
    private static readonly Lazy<Bytecode> CompiledBytecode = new(CompileAll, LazyThreadSafetyMode.ExecutionAndPublication);

    public ID3D11VertexShader VertexShader { get; }
    public ID3D11PixelShader PatternShader { get; }
    public ID3D11PixelShader SampleShader { get; }
    public ID3D11PixelShader YCoCgShader { get; }
    public ID3D11Buffer TimeBuffer { get; }      // b0: pattern time, written per Draw
    public ID3D11Buffer UvBuffer { get; }        // b1: per-output UV sub-rect, written per Draw (M7)
    public ID3D11RasterizerState RasterizerState { get; }
    public ID3D11SamplerState Sampler { get; }

    public QuadPipeline(ID3D11Device device)
    {
        var code = CompiledBytecode.Value;
        ID3D11VertexShader? vs = null;
        ID3D11PixelShader? pattern = null, sample = null, ycocg = null;
        ID3D11Buffer? time = null, uv = null;
        ID3D11RasterizerState? rasterizer = null;
        try
        {
            vs = device.CreateVertexShader(code.Vertex);
            pattern = device.CreatePixelShader(code.Pattern);
            sample = device.CreatePixelShader(code.Sample);
            ycocg = device.CreatePixelShader(code.YCoCg);
            time = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
            uv = device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
            rasterizer = device.CreateRasterizerState(RasterizerDescription.CullNone);
            Sampler = device.CreateSamplerState(SamplerDescription.LinearClamp);
        }
        catch
        {
            // Partial construction must not leak device children (they would pin the device / show up
            // as live objects). Release what was made, then surface the error.
            rasterizer?.Dispose(); uv?.Dispose(); time?.Dispose();
            ycocg?.Dispose(); sample?.Dispose(); pattern?.Dispose(); vs?.Dispose();
            throw;
        }
        VertexShader = vs;
        PatternShader = pattern;
        SampleShader = sample;
        YCoCgShader = ycocg;
        TimeBuffer = time;
        UvBuffer = uv;
        RasterizerState = rasterizer;
    }

    public void Dispose()
    {
        Sampler.Dispose();
        RasterizerState.Dispose();
        UvBuffer.Dispose();
        TimeBuffer.Dispose();
        YCoCgShader.Dispose();
        SampleShader.Dispose();
        PatternShader.Dispose();
        VertexShader.Dispose();
    }

    private static Bytecode CompileAll()
    {
        var source = LoadShaderSource();
        return new Bytecode(
            Compile(source, "VSMain", "vs_5_0"),
            Compile(source, "PSMain", "ps_5_0"),
            Compile(source, "PSSample", "ps_5_0"),
            Compile(source, "PSSampleYCoCg", "ps_5_0"));
    }

    private static string LoadShaderSource()
    {
        const string resourceName = "MultiMon.Graphics.Shaders.Quad.hlsl";
        using var stream = typeof(QuadPipeline).Assembly.GetManifestResourceStream(resourceName)
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
