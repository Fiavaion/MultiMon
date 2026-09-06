using System.Diagnostics;
using System.Runtime.InteropServices;
using Metal;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Sync;
using MultiMon.Decode.Mac.Hap;
using MultiMon.Graphics.Mac;
using MultiMon.Hap;

namespace MultiMon.Stress.Mac;

/// <summary>
/// The source-sampling gate (<c>--source-check</c>): proves the decode→render handoff end to end through the
/// REAL path — a <see cref="DecodedFrame"/> published into a <see cref="FrameTimeline"/> that a
/// <see cref="FullscreenQuadPass"/> is bound to, drawn by the render thread with the shared
/// <c>QuadPipeline</c> into an offscreen target, read back and compared pixel-exact. Four quadrant UV rects
/// over a four-colour texture prove the uniform layout (a shader/C# mismatch collapses every quadrant to one
/// texel); one synthetic HapQ YCoCg texel proves the conversion shader; and, given a BC1/BC3/HapQ clip, the REAL
/// HAP path — a <see cref="HapSource"/>'s mid-clip frame uploaded to a pooled BCn texture, blitted and sampled
/// through a texel-exact window — must put the CPU-decoded reference colour (<see cref="BcnReference"/>) at the
/// target's centre for TWO texels: the most chromatic one (a swapped chroma channel, flipped sign or dropped
/// scale divide changes it) and the brightest one (luma). A BC4/BC7 clip has no CPU reference and gets only a
/// non-clear + non-uniform frame check. Exit 0 = every check PASS.
/// </summary>
internal static class SourceCheck
{
    private const int TargetSize = 64;
    /// <summary>Per-channel slack for the HAP comparisons: GPU BCn decoders may round the 1/3–2/3 endpoint blends
    /// differently from the CPU reference. A chroma fault that moves a texel by no more than this is invisible.</summary>
    private const int HapTolerance = 8;

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

    /// <summary><paramref name="hapClip"/>: the HAP .mov for the hap checks, or null to skip them (noted).</summary>
    public static int Run(ILog log, string? hapClip)
    {
        log.Info("Stress", "source-check: DecodedFrame -> FrameTimeline -> FullscreenQuadPass.Draw -> offscreen target -> readback");
        // The check count is fixed BEFORE anything runs so a crash mid-way can never leave passed == expected.
        var demux = hapClip is null ? null : MovHapDemuxer.Parse(hapClip);
        var checkCount = 5 + (demux is null ? 0 : BcnReference.Supports(demux.DeclaredFormat) ? 2 : 1);
        var passedChecks = 0;
        var provider = new GraphicsDeviceProvider(log);
        provider.Acquire();
        var loop = new RenderLoop(provider, log);

        IMTLTexture? quadrantTexture = null, ycocgTexture = null, target = null;
        IMTLBuffer? readback = null, uniforms = null;
        MTLRenderPassDescriptor? renderPass = null;
        FullscreenQuadPass? quadrantPass = null, ycocgPass = null, hapPass = null;
        FrameTimeline? quadrantTimeline = null, ycocgTimeline = null;
        HapSource? hapSource = null;
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

            // Checks 6-7: the real HAP path. The source decodes into pooled BCn textures; draws at the MID-CLIP sample's
            // exact PTS (frame 0 of a typical clip is a black fade-in, which would prove nothing) select the greatest
            // PTS <= target, pruning earlier frames so the blocked decoder advances, until that frame is published
            // and selected; it is blitted into the pass's BCn texture. The same sample is decoded on the CPU
            // (BC1/BC3/HapQ) and its most chromatic + brightest texels found; a draw per texel samples a 64-texel
            // window centred on it so the target's centre fragment lands exactly on that texel — the pixel must not
            // be the clear colour and must match the CPU reference. BC4/BC7 have no reference and settle for a
            // non-clear, non-uniform full frame.
            if (hapClip is not null)
            {
                var sampleIndex = demux!.Samples.Count / 2;
                var targetPts = TimeSpan.FromTicks(demux.Samples[sampleIndex].PtsTicks);
                hapSource = new HapSource(hapClip, provider, log, "check");
                hapPass = new FullscreenQuadPass(provider);
                hapPass.BindSource(hapSource.Frames, hapSource.Width, hapSource.Height, hapSource.TextureFormat, hapSource.UseYCoCg);
                hapSource.Start();
                if (!SpinWait.SpinUntil(() => hapSource.DecodedFrames > 0 || hapSource.IsFaulted, TimeSpan.FromSeconds(5)) || hapSource.IsFaulted)
                    throw new InvalidOperationException($"HAP source published no frame within 5s (faulted={hapSource.IsFaulted}).");
                var draws = 0;
                var budget = Stopwatch.StartNew();
                do
                {
                    RenderTarget(loop, provider, hapPass, renderPass, uniforms, target, readback, UvRect.Full, targetPts);
                    draws++;
                } while (hapSource.CurrentPts < targetPts && !hapSource.IsFaulted && budget.Elapsed < TimeSpan.FromSeconds(10));
                if (hapSource.CurrentPts < targetPts)
                    throw new InvalidOperationException($"HAP source never reached sample {sampleIndex} (pts {targetPts.TotalSeconds:0.000}s) after {draws} draws in {budget.Elapsed.TotalSeconds:0.0}s (decoded={hapSource.DecodedFrames} faulted={hapSource.IsFaulted}).");
                if (BcnReference.Supports(demux.DeclaredFormat))
                {
                    var sample = demux.Samples[sampleIndex];
                    var compressed = new byte[sample.Size];
                    using (var file = File.OpenRead(hapClip))
                    {
                        file.Position = sample.FileOffset;
                        file.ReadExactly(compressed);
                    }
                    var decoded = HapFrameDecoder.Decode(compressed);
                    int w = hapSource.Width, h = hapSource.Height;
                    var (chromatic, brightest) = BcnReference.FindReferenceTexels(decoded.Data, decoded.Format, w, h, step: 4);
                    log.Info("Stress", $"  hap sample {sampleIndex} (pts {targetPts.TotalSeconds:0.000}s, {draws} draws to reach it) of {hapClip}: " +
                                       $"most chromatic texel {chromatic}, brightest texel {brightest}");
                    foreach (var ((tx, ty), kind) in new[] { (chromatic, "most chromatic"), (brightest, "brightest") })
                    {
                        // A TargetSize-texel window whose centre fragment (i.uv = 32.5/64) samples texel (tx, ty) exactly:
                        // x = (x0 + 32.5) - 0.5 = tx when x0 = tx - 32 (clamped into the frame, then re-derived).
                        var x0 = Math.Clamp(tx - TargetSize / 2, 0, w - TargetSize);
                        var y0 = Math.Clamp(ty - TargetSize / 2, 0, h - TargetSize);
                        var window = new UvRect((float)x0 / w, (float)y0 / h, (float)(x0 + TargetSize) / w, (float)(y0 + TargetSize) / h);
                        var u = (x0 + TargetSize / 2 + 0.5f) / w;
                        var v = (y0 + TargetSize / 2 + 0.5f) / h;
                        var reference = BcnReference.Sample(decoded.Data, decoded.Format, w, h, u, v);
                        if (kind == "most chromatic" && BcnReference.ChromaSensitivity(reference) <= HapTolerance)
                            log.Info("Stress", $"  NOTE: the most chromatic texel decodes to ({reference.R},{reference.G},{reference.B}), chroma sensitivity {BcnReference.ChromaSensitivity(reference)} <= ±{HapTolerance} — " +
                                               $"this clip is (near) greyscale at sample {sampleIndex}, so this run does NOT prove the chroma channels/sign/scale; use a colour HapQ clip for that.");
                        var pixels = RenderTarget(loop, provider, hapPass, renderPass, uniforms, target, readback, window, targetPts);
                        var centre = Pixel(pixels, TargetSize / 2, TargetSize / 2);
                        var nonClear = centre != ((byte)0, (byte)0, (byte)0);
                        var match = Report(log, $"hap {kind} texel ({x0 + TargetSize / 2},{y0 + TargetSize / 2}) in window {window} vs CPU {decoded.Format} reference", reference, centre, HapTolerance);
                        passedChecks += match == 1 && nonClear ? 1 : 0;
                        if (!nonClear)
                            log.Error("Stress", $"  FAIL hap {kind} centre is the clear colour (0,0,0).");
                    }
                    hapSource.Stop();
                }
                else
                {
                    // Unexercised: no fixture in the media set is BC4 (HAP Alpha) or BC7 (HAP R); this branch has never run.
                    var pixels = RenderTarget(loop, provider, hapPass, renderPass, uniforms, target, readback, UvRect.Full, targetPts);
                    hapSource.Stop();
                    var centre = Pixel(pixels, TargetSize / 2, TargetSize / 2);
                    var corners = new[] { Pixel(pixels, 0, 0), Pixel(pixels, TargetSize - 1, 0), Pixel(pixels, 0, TargetSize - 1), Pixel(pixels, TargetSize - 1, TargetSize - 1) };
                    var nonClear = centre != ((byte)0, (byte)0, (byte)0);
                    var nonUniform = corners.Any(c => c != centre) || corners.Distinct().Count() > 1;
                    log.Info("Stress", $"  NOTE: no CPU reference for {demux.DeclaredFormat}; the hap check is non-clear + non-uniform only. " +
                                       $"centre=({centre.R},{centre.G},{centre.B}) corners=[{string.Join(" ", corners.Select(c => $"({c.R},{c.G},{c.B})"))}]");
                    passedChecks += Report(log, $"hap sample {sampleIndex} non-clear + non-uniform", nonClear && nonUniform);
                }
            }
            else
                log.Info("Stress", "NOTE: no HAP clip (--video or MULTIMON_HAP_FIXTURE) — the hap check is skipped.");
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
            hapSource?.Stop();
            quadrantPass?.Dispose();
            ycocgPass?.Dispose();
            hapPass?.Dispose();
            quadrantTimeline?.Dispose();
            ycocgTimeline?.Dispose();
            hapSource?.Dispose();
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

    private static (byte R, byte G, byte B) RenderCentrePixel(RenderLoop loop, GraphicsDeviceProvider provider,
        FullscreenQuadPass pass, MTLRenderPassDescriptor renderPass, IMTLBuffer uniforms, IMTLTexture target, IMTLBuffer readback, UvRect uv) =>
        Pixel(RenderTarget(loop, provider, pass, renderPass, uniforms, target, readback, uv), TargetSize / 2, TargetSize / 2);

    private static (byte R, byte G, byte B) Pixel(byte[] bgra, int x, int y)
    {
        var i = (y * TargetSize + x) * 4;
        return (bgra[i + 2], bgra[i + 1], bgra[i]); // BGRA8 memory order
    }

    /// <summary>Draws <paramref name="pass"/> through <paramref name="uv"/> on the render thread, blits the
    /// target into the readback buffer, waits for completion, and returns the whole target as BGRA8 bytes.</summary>
    private static byte[] RenderTarget(RenderLoop loop, GraphicsDeviceProvider provider,
        FullscreenQuadPass pass, MTLRenderPassDescriptor renderPass, IMTLBuffer uniforms, IMTLTexture target, IMTLBuffer readback, UvRect uv,
        TimeSpan mediaTime = default)
    {
        if (!loop.Invoke(() =>
        {
            var commandBuffer = provider.CommandQueue.CommandBuffer()
                ?? throw new InvalidOperationException("command buffer creation failed.");
            try
            {
                var read = pass.Draw(commandBuffer, renderPass, uniforms, 0, TargetSize, TargetSize, mediaTime, uv);
                var blit = commandBuffer.BlitCommandEncoder ?? throw new InvalidOperationException("blit encoder creation failed.");
                blit.CopyFromTexture(target, 0, 0, new MTLOrigin(0, 0, 0), new MTLSize(TargetSize, TargetSize, 1),
                    readback, 0, TargetSize * 4, TargetSize * TargetSize * 4);
                blit.EndEncoding();
                blit.Dispose();
                read?.HoldUntilCompleted(commandBuffer); // the committer's hold, immediately before Commit (see FullscreenQuadPass.Draw)
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

        var bytes = new byte[TargetSize * TargetSize * 4];
        Marshal.Copy(readback.Contents, bytes, 0, bytes.Length);
        return bytes;
    }

    private static int Report(ILog log, string name, bool ok)
    {
        log.Info("Stress", $"  {(ok ? "PASS" : "FAIL")} {name}");
        return ok ? 1 : 0;
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
