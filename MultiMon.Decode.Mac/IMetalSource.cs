using Metal;
using MultiMon.Core.Abstractions;
using MultiMon.Graphics.Mac;

namespace MultiMon.Decode.Mac;

/// <summary>
/// The Mac decode source contract the render side binds to: <see cref="ISource"/>'s lifecycle plus what a
/// <see cref="FullscreenQuadPass"/> needs to create its persistent texture (the timeline, the frame size and
/// the published texture format) and what the stress harness gates (frames decoded, textures still held by
/// in-flight GPU work at dispose). Implemented by the HAP and VideoToolbox sources; the decode ladder
/// (<see cref="SourceLadder"/>) hands one back without the caller knowing which.
/// </summary>
public interface IMetalSource : ISource
{
    /// <summary>The decode→render handoff. Owned by the source; the render-side pass borrows from it.</summary>
    FrameTimeline Frames { get; }

    /// <summary>Texture size the pass must create — every published frame is exactly this.</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>The Metal format of every published frame (the pass's persistent texture uses the same).</summary>
    MTLPixelFormat TextureFormat { get; }

    /// <summary>True when the pass must decode HapQ scaled YCoCg in the shader.</summary>
    bool UseYCoCg { get; }

    /// <summary>Frames decoded and published since Start (thread-safe read) — the harness's advance check.</summary>
    long DecodedFrames { get; }

    /// <summary>Frame textures still held by in-flight GPU work when Dispose ran — non-zero means the fence and
    /// the teardown order disagree; the harness fails on it.</summary>
    int OutstandingAtDispose { get; }
}
