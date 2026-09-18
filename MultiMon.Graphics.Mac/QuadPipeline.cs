using Foundation;
using Metal;

namespace MultiMon.Graphics.Mac;

/// <summary>
/// The fullscreen-quad shader library + pipeline-state set shared by EVERY <see cref="FullscreenQuadPass"/> on
/// the device: the library compiled from the embedded MSL source, the three render pipeline states (test
/// pattern / passthrough sample / HapQ YCoCg) targeting the BGRA8 drawable format, and the linear-clamp
/// sampler. Built ONCE per device by <see cref="GraphicsDeviceProvider"/> and disposed with the device graph —
/// never per pass, never per cycle (LESSON-ARCH-002). Read only by the render thread during Draw.
/// </summary>
internal sealed class QuadPipeline : IDisposable
{
    public const MTLPixelFormat DrawableFormat = MTLPixelFormat.BGRA8Unorm;

    private readonly MetalResourceTracker _tracker;
    private readonly IMTLLibrary _library;

    public IMTLRenderPipelineState PatternPipeline { get; }
    public IMTLRenderPipelineState SamplePipeline { get; }
    public IMTLRenderPipelineState YCoCgPipeline { get; }
    public IMTLSamplerState Sampler { get; }

    public QuadPipeline(IMTLDevice device, MetalResourceTracker tracker)
    {
        _tracker = tracker;
        using var compileOptions = new MTLCompileOptions();
        _library = device.CreateLibrary(LoadShaderSource(), compileOptions, out NSError? error)
            ?? throw new InvalidOperationException($"Metal shader library compile failed: {error?.LocalizedDescription}");
        tracker.LibraryCreated();

        IMTLRenderPipelineState? pattern = null, sample = null, ycocg = null;
        try
        {
            pattern = CreatePipeline(device, "pattern_main");
            sample = CreatePipeline(device, "sample_main");
            ycocg = CreatePipeline(device, "sample_ycocg_main");

            using var samplerDescriptor = new MTLSamplerDescriptor
            {
                MinFilter = MTLSamplerMinMagFilter.Linear,
                MagFilter = MTLSamplerMinMagFilter.Linear,
                SAddressMode = MTLSamplerAddressMode.ClampToEdge,
                TAddressMode = MTLSamplerAddressMode.ClampToEdge
            };
            Sampler = device.CreateSamplerState(samplerDescriptor)
                ?? throw new InvalidOperationException("Metal sampler state creation failed.");
            tracker.SamplerCreated();
        }
        catch
        {
            // Partial construction must not leak device objects: release what was made, then surface the error.
            DisposePipeline(ycocg); DisposePipeline(sample); DisposePipeline(pattern);
            _library.Dispose();
            tracker.LibraryDisposed();
            throw;
        }
        PatternPipeline = pattern;
        SamplePipeline = sample;
        YCoCgPipeline = ycocg;
    }

    private IMTLRenderPipelineState CreatePipeline(IMTLDevice device, string fragmentFunction)
    {
        using var vertex = _library.CreateFunction("quad_vertex")
            ?? throw new InvalidOperationException("Metal function 'quad_vertex' not found in the quad library.");
        using var fragment = _library.CreateFunction(fragmentFunction)
            ?? throw new InvalidOperationException($"Metal function '{fragmentFunction}' not found in the quad library.");
        using var descriptor = new MTLRenderPipelineDescriptor
        {
            Label = fragmentFunction,
            VertexFunction = vertex,
            FragmentFunction = fragment
        };
        descriptor.ColorAttachments[0].PixelFormat = DrawableFormat;
        var state = device.CreateRenderPipelineState(descriptor, out NSError? error)
            ?? throw new InvalidOperationException($"Metal pipeline state '{fragmentFunction}' failed: {error?.LocalizedDescription}");
        _tracker.PipelineStateCreated();
        return state;
    }

    private void DisposePipeline(IMTLRenderPipelineState? state)
    {
        if (state is null) return;
        state.Dispose();
        _tracker.PipelineStateDisposed();
    }

    private static string LoadShaderSource()
    {
        const string resourceName = "MultiMon.Graphics.Mac.Shaders.Quad.metal";
        using var stream = typeof(QuadPipeline).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded shader '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        Sampler.Dispose();
        _tracker.SamplerDisposed();
        DisposePipeline(YCoCgPipeline);
        DisposePipeline(SamplePipeline);
        DisposePipeline(PatternPipeline);
        _library.Dispose();
        _tracker.LibraryDisposed();
    }
}
