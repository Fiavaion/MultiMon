using Vortice.Direct3D11;

namespace MultiMon.Graphics;

/// <summary>
/// Owns the single, process-wide <see cref="ID3D11Device"/>, reference-counted. The persistent
/// pipeline (device + per-monitor swapchains + shaders + state) is built ONCE and reused for the
/// whole session — entering/leaving perform mode never creates or destroys it.
///
/// This interface lives in MultiMon.Graphics (not Core) because it exposes a Vortice/D3D11 type;
/// keeping it out of Core preserves Core as a pure, graphics-free assembly.
///
/// Implemented by <see cref="GraphicsDeviceProvider"/>.
/// </summary>
public interface IGraphicsDeviceProvider : IDisposable
{
    /// <summary>The one shared device. Valid between the first <see cref="Acquire"/> and the last <see cref="Release"/>.</summary>
    ID3D11Device Device { get; }

    /// <summary>Increment the reference count, creating the device on the first acquire.</summary>
    void Acquire();

    /// <summary>Decrement the reference count, disposing the device only when it reaches zero — off the UI thread.</summary>
    void Release();
}
