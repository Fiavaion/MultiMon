using System.Runtime.InteropServices;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Sync;
using MultiMon.Graphics.Mac;

namespace MultiMon.Stress.Mac;

/// <summary>
/// The source-sampling gate (<c>--source-check</c>): proves the decode→render handoff end to end through the
/// REAL path — a <see cref="DecodedFrame"/> published into a <see cref="FrameTimeline"/> that a
/// <see cref="FullscreenQuadPass"/> is bound to, drawn by the render thread with the shared
/// <c>QuadPipeline</c> into an offscreen target, read back and compared pixel-exact. Four quadrant UV rects
/// over a four-colour texture prove the uniform layout (a shader/C# mismatch collapses every quadrant to one
/// texel); one HapQ YCoCg texel proves the conversion shader. Exit 0 = every check PASS.
/// </summary>
internal static class SourceCheck
{
    private const int TargetSize = 64;

    /// <summary>
    /// The four quadrant colours as (r, g, b): top-left red, top-right green, bottom-left blue, bottom-right
    /// white. Laid out as a 4x4 texture with a 2x2 block per colour so the pipeline's LINEAR sampler, reading
    /// the target's centre pixel (a half-pixel off any texel centre), blends two texels of the SAME colour and
    /// the comparison stays exact instead of tolerating a filter bleed.
    /// </summary>
    private static readonly (byte R, byte G, byte B)[] QuadrantColours =
    [
        (255, 0, 0), (0, 255, 0),
        (0, 0, 255), (255, 255, 255)
    ];
    public const int QuadrantTextureSize = 4;

    /// <summary>The 4x4 BGRA8 quadrant texture, uploaded once (tracked; the caller disposes + counts it out).</summary>
    public static IMTLTexture CreateQuadrantTexture(GraphicsDeviceProvider provider)
    {
        var pixels = new byte[QuadrantTextureSize * QuadrantTextureSize * 4];
        for (var y = 0; y < QuadrantTextureSize; y++)
            for (var x = 0; x < QuadrantTextureSize; x++)
            {
                var (r, g, b) = QuadrantColours[(y / 2) * 2 + (x / 2)];
                var i = (y * QuadrantTextureSize + x) * 4;
                pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; // BGRA8 byte order
            }
        return CreateUploadedTexture(provider, QuadrantTextureSize, pixels);
    }

    public static int Run(ILog log)
    {
        log.Info("Stress", "source-check: DecodedFrame -> FrameTimeline -> FullscreenQuadPass.Draw -> offscreen target -> readback");
        var provider = new GraphicsDeviceProvider(log);
        provider.Acquire();
        var loop = new RenderLoop(provider, log);
        const int checkCount = 5;
        var passedChecks = 0;

        IMTLTexture? quadrantTexture = null, ycocgTexture = null, target = null;
        IMTLBuffer? readback = null, uniforms = null;
        MTLRenderPassDescriptor? renderPass = null;
        FullscreenQuadPass? quadrantPass = null, ycocgPass = null;
        FrameTimeline? quadrantTimeline = null, ycocgTimeline = null;
        try
        {
            loop.Start();

            // Offscreen target + readback, created once for all five draws.
            using (var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(QuadPipeline.DrawableFormat, TargetSize, TargetSize, false))
            {
                descriptor.Usage = MTLTextureUsage.RenderTarget;
                descriptor.StorageMode = MTLStorageMode.Private;
                target = provider.Device.CreateTexture(descriptor) ?? throw new InvalidOperationException("offscreen target creation failed.");
                provider.Tracker.TextureCreated();
            }
            readback = provider.Device.CreateBuffer(TargetSize * TargetSize * 4, MTLResourceOptions.StorageModeShared)
                ?? throw new InvalidOperationException("readback buffer creation failed.");
            provider.Tracker.BufferCreated();
            uniforms = provider.Device.CreateBuffer(FullscreenQuadPass.UniformSlotStride, MTLResourceOptions.StorageModeShared)
                ?? throw new InvalidOperationException("uniform buffer creation failed.");
            provider.Tracker.BufferCreated();
            renderPass = new MTLRenderPassDescriptor();
            var attachment = renderPass.ColorAttachments[0];
            attachment.Texture = target;
            attachment.LoadAction = MTLLoadAction.Clear;
            attachment.StoreAction = MTLStoreAction.Store;
            attachment.ClearColor = new MTLClearColor(0, 0, 0, 1);

            // Check 1-4: the four quadrant UV rects each land on their own colour.
            quadrantTexture = CreateQuadrantTexture(provider);
            quadrantTimeline = new FrameTimeline();
            quadrantTimeline.Publish(new DecodedFrame(quadrantTexture, TimeSpan.Zero, () => { }));
            quadrantPass = new FullscreenQuadPass(provider);
            quadrantPass.BindSource(quadrantTimeline, QuadrantTextureSize, QuadrantTextureSize, MTLPixelFormat.BGRA8Unorm);
            for (var row = 0; row < 2; row++)
                for (var col = 0; col < 2; col++)
                {
                    var uv = UvLayout.Quadrant(row, col, 2, 2);
                    var expected = QuadrantColours[row * 2 + col];
                    var actual = RenderCentrePixel(loop, provider, quadrantPass, renderPass, uniforms, target, readback, uv);
                    passedChecks += Report(log, $"quadrant ({row},{col}) uv={uv}", expected, actual, tolerance: 0);
                }

            // Check 5: one known RGB encoded as HapQ scaled CoCgY (Co in R, Cg in G, scale in B, Y in A), decoded
            // by sample_ycocg_main. Inverse of the shader: Y=(R+2G+B)/4, Co=(R-B)/2, Cg=(2G-R-B)/4, then the
            // chroma is multiplied by scale = B*(255/8)+1 and offset by 128/255. RGB (180,140,60) → Y=130,
            // Co=60, Cg=10; with the scale byte 8 (scale 2): Co→120+128=248, Cg→20+128=148.
            var ycocgExpected = ((byte)180, (byte)140, (byte)60);
            // Shader channels r=Co(248) g=Cg(148) b=scale(8) a=Y(130); BGRA8 memory order is B,G,R,A.
            ycocgTexture = CreateUploadedTexture(provider, 2, Enumerable.Repeat(new byte[] { 8, 148, 248, 130 }, 4).SelectMany(b => b).ToArray());
            ycocgTimeline = new FrameTimeline();
            ycocgTimeline.Publish(new DecodedFrame(ycocgTexture, TimeSpan.Zero, () => { }));
            ycocgPass = new FullscreenQuadPass(provider);
            ycocgPass.BindSource(ycocgTimeline, 2, 2, MTLPixelFormat.BGRA8Unorm, useYCoCg: true);
            var ycocgActual = RenderCentrePixel(loop, provider, ycocgPass, renderPass, uniforms, target, readback, UvRect.Full);
            passedChecks += Report(log, "ycocg (180,140,60) via scaled CoCgY (248,148,8,130)", ycocgExpected, ycocgActual, tolerance: 2);
        }
        catch (Exception ex)
        {
            log.Error("Stress", $"source-check crashed: {ex}");
        }
        finally
        {
            // Teardown order: render loop stopped and joined → passes (persistent textures) → timelines (frames)
            // → the check's own textures/buffers → device.
            loop.Stop();
            quadrantPass?.Dispose();
            ycocgPass?.Dispose();
            quadrantTimeline?.Dispose();
            ycocgTimeline?.Dispose();
            renderPass?.Dispose();
            DisposeTexture(provider, quadrantTexture);
            DisposeTexture(provider, ycocgTexture);
            DisposeTexture(provider, target);
            DisposeBuffer(provider, readback);
            DisposeBuffer(provider, uniforms);
            provider.Release();
            provider.Dispose();
            log.Info("Stress", $"teardown complete: {provider.Tracker}");
        }

        var leaked = provider.Tracker.LiveCount != 0;
        if (leaked)
            log.Error("Stress", $"{provider.Tracker.LiveCount} tracked object(s) survived teardown.");
        var passed = passedChecks == checkCount && !leaked;
        log.Info("Stress", $"RESULT: {(passed ? "PASS" : "FAIL")} — source-check {passedChecks}/{checkCount}{(leaked ? ", tracked-object leak" : "")}.");
        return passed ? 0 : 1;
    }

    /// <summary>Draws <paramref name="pass"/> through <paramref name="uv"/> on the render thread, blits the
    /// target into the readback buffer, waits for completion, and returns the centre pixel as (r, g, b).</summary>
    private static (byte R, byte G, byte B) RenderCentrePixel(RenderLoop loop, GraphicsDeviceProvider provider,
        FullscreenQuadPass pass, MTLRenderPassDescriptor renderPass, IMTLBuffer uniforms, IMTLTexture target, IMTLBuffer readback, UvRect uv)
    {
        if (!loop.Invoke(() =>
        {
            var commandBuffer = provider.CommandQueue.CommandBuffer()
                ?? throw new InvalidOperationException("command buffer creation failed.");
            try
            {
                pass.Draw(commandBuffer, renderPass, uniforms, 0, TargetSize, TargetSize, TimeSpan.Zero, uv);
                var blit = commandBuffer.BlitCommandEncoder ?? throw new InvalidOperationException("blit encoder creation failed.");
                blit.CopyFromTexture(target, 0, 0, new MTLOrigin(0, 0, 0), new MTLSize(TargetSize, TargetSize, 1),
                    readback, 0, TargetSize * 4, TargetSize * TargetSize * 4);
                blit.EndEncoding();
                blit.Dispose();
                commandBuffer.Commit();
                commandBuffer.WaitUntilCompleted();
                if (commandBuffer.Status != MTLCommandBufferStatus.Completed)
                    throw new InvalidOperationException($"command buffer ended in {commandBuffer.Status}: {commandBuffer.Error?.LocalizedDescription}");
            }
            finally
            {
                commandBuffer.Dispose();
            }
        }))
            throw new InvalidOperationException("render thread not alive.");

        var centre = (TargetSize / 2 * TargetSize + TargetSize / 2) * 4;
        var bytes = new byte[4];
        Marshal.Copy(readback.Contents + centre, bytes, 0, 4);
        return (bytes[2], bytes[1], bytes[0]); // BGRA8 memory order
    }

    /// <summary>Logs one check; returns 1 when it passed, 0 when it failed.</summary>
    private static int Report(ILog log, string name, (byte R, byte G, byte B) expected, (byte R, byte G, byte B) actual, int tolerance)
    {
        var ok = Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance;
        log.Info("Stress", $"  {(ok ? "PASS" : "FAIL")} {name}: expected ({expected.R},{expected.G},{expected.B}) got ({actual.R},{actual.G},{actual.B})" +
                           (tolerance > 0 ? $" (±{tolerance})" : ""));
        return ok ? 1 : 0;
    }

    /// <summary>A square BGRA8 texture filled by ONE replaceRegion from <paramref name="pixels"/> (BGRA byte order).</summary>
    private static IMTLTexture CreateUploadedTexture(GraphicsDeviceProvider provider, int size, byte[] pixels)
    {
        using var descriptor = MTLTextureDescriptor.CreateTexture2DDescriptor(MTLPixelFormat.BGRA8Unorm, (nuint)size, (nuint)size, false);
        descriptor.Usage = MTLTextureUsage.ShaderRead;
        // CPU-written textures are Shared on unified memory, Managed on discrete-GPU (Intel) Macs.
        descriptor.StorageMode = provider.Device.HasUnifiedMemory ? MTLStorageMode.Shared : MTLStorageMode.Managed;
        var texture = provider.Device.CreateTexture(descriptor) ?? throw new InvalidOperationException($"{size}x{size} BGRA8 texture creation failed.");
        provider.Tracker.TextureCreated();
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            texture.ReplaceRegion(MTLRegion.Create2D(0, 0, size, size), 0, handle.AddrOfPinnedObject(), (nuint)(size * 4));
        }
        finally
        {
            handle.Free();
        }
        return texture;
    }

    private static void DisposeTexture(GraphicsDeviceProvider provider, IMTLTexture? texture)
    {
        if (texture is null) return;
        texture.Dispose();
        provider.Tracker.TextureDisposed();
    }

    private static void DisposeBuffer(GraphicsDeviceProvider provider, IMTLBuffer? buffer)
    {
        if (buffer is null) return;
        buffer.Dispose();
        provider.Tracker.BufferDisposed();
    }
}
