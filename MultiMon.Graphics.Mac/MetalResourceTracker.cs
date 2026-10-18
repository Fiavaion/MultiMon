namespace MultiMon.Graphics.Mac;

/// <summary>
/// The Mac twin of the D3D11 debug-layer live-object count: every Metal object this assembly creates is
/// counted in and every one it disposes is counted out, so the stress harness can diff the total across
/// perform cycles — any sustained growth is an ownership bug (LESSON-BUG-001), never something to mask.
/// Drawables are counted while in flight (acquired from the layer until their command buffer completed).
/// Thread-safe: counters are Interlocked; the render, decode and Metal completion threads all touch them.
/// </summary>
public sealed class MetalResourceTracker
{
    private long _textures, _buffers, _pipelineStates, _libraries, _samplers, _commandQueues, _drawablesInFlight;

    public long Textures => Volatile.Read(ref _textures);
    public long Buffers => Volatile.Read(ref _buffers);
    public long PipelineStates => Volatile.Read(ref _pipelineStates);
    public long Libraries => Volatile.Read(ref _libraries);
    public long Samplers => Volatile.Read(ref _samplers);
    public long CommandQueues => Volatile.Read(ref _commandQueues);
    public long DrawablesInFlight => Volatile.Read(ref _drawablesInFlight);

    /// <summary>Every tracked object alive right now — the number the harness diffs across cycles.</summary>
    public long LiveCount => Textures + Buffers + PipelineStates + Libraries + Samplers + CommandQueues + DrawablesInFlight;

    public void TextureCreated() => Interlocked.Increment(ref _textures);
    public void TextureDisposed() => Interlocked.Decrement(ref _textures);
    public void BufferCreated() => Interlocked.Increment(ref _buffers);
    public void BufferDisposed() => Interlocked.Decrement(ref _buffers);
    public void PipelineStateCreated() => Interlocked.Increment(ref _pipelineStates);
    public void PipelineStateDisposed() => Interlocked.Decrement(ref _pipelineStates);
    public void LibraryCreated() => Interlocked.Increment(ref _libraries);
    public void LibraryDisposed() => Interlocked.Decrement(ref _libraries);
    public void SamplerCreated() => Interlocked.Increment(ref _samplers);
    public void SamplerDisposed() => Interlocked.Decrement(ref _samplers);
    public void CommandQueueCreated() => Interlocked.Increment(ref _commandQueues);
    public void CommandQueueDisposed() => Interlocked.Decrement(ref _commandQueues);
    public void DrawableAcquired() => Interlocked.Increment(ref _drawablesInFlight);
    public void DrawableCompleted() => Interlocked.Decrement(ref _drawablesInFlight);

    public override string ToString() =>
        $"live={LiveCount} (textures={Textures} buffers={Buffers} pipelines={PipelineStates} libraries={Libraries} " +
        $"samplers={Samplers} queues={CommandQueues} drawablesInFlight={DrawablesInFlight})";
}
