namespace MultiMon.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer (SPSC) ring buffer of <see cref="float"/> audio samples.
///
/// <para><b>Thread contract:</b> exactly ONE thread calls <see cref="Write"/> (the MF decode thread) and
/// exactly ONE other thread calls <see cref="Read"/> (the WASAPI render thread). Both may run concurrently
/// without locks or allocation on the hot path.</para>
///
/// <para><b>Memory ordering:</b> <c>_writePos</c> is written by the producer and read by the consumer;
/// <c>_readPos</c> is written by the consumer and read by the producer. Both use
/// <see cref="Volatile.Read{T}"/>/<see cref="Volatile.Write{T}"/> (acquire/release semantics on .NET)
/// to guarantee that the data copy into the backing array is visible to the consumer <em>before</em>
/// the updated <c>_writePos</c> is, and symmetrically that the consumer's copy-out is complete before
/// the slot is returned to the producer via <c>_readPos</c>. This establishes the correct
/// happens-before edges without a lock.</para>
///
/// <para><b>Index scheme:</b> <c>_writePos</c> and <c>_readPos</c> are free-running <see langword="long"/>
/// counters (never masked until indexing). <c>count = writePos - readPos</c>; free slots =
/// <c>_totalSlots - count</c>. Because the buffer size is a power of two,
/// <c>index = pos &amp; (_totalSlots - 1)</c> replaces modulo. This scheme uses the <em>full</em>
/// buffer (unlike the waste-one-slot sentinel scheme).</para>
///
/// <para><b>Wrap handling:</b> a write or read that crosses the linear end of the array is split into
/// at most two <see cref="Array.Copy"/> segments.</para>
/// </summary>
public sealed class AudioRing
{
    private readonly float[] _buf;
    private readonly int     _totalSlots; // power of two; == _buf.Length
    private readonly int     _mask;       // _totalSlots - 1

    // Free-running counters: never wrap, masked only when indexing into _buf.
    // _writePos: written by producer, read by consumer.
    // _readPos:  written by consumer, read by producer.
    private long _writePos;
    private long _readPos;

    /// <summary>Number of interleaved channels per sample-frame (e.g. 2 for stereo).</summary>
    public int Channels { get; }

    /// <summary>
    /// Capacity in sample-frames (each frame = <see cref="Channels"/> floats).
    /// Equals the power-of-two slot count divided by <see cref="Channels"/>.
    /// </summary>
    public int CapacityFrames { get; }

    /// <summary>
    /// Initialises the ring buffer.
    /// </summary>
    /// <param name="capacityFrames">
    /// Desired capacity in sample-frames. The actual capacity is rounded up to the next power of two
    /// (in floats) divided by <paramref name="channels"/>, so <see cref="CapacityFrames"/> may be
    /// slightly larger than the requested value.
    /// </param>
    /// <param name="channels">Interleaved channel count per frame (e.g. 2 for stereo).</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="capacityFrames"/> or <paramref name="channels"/> is &lt;= 0.
    /// </exception>
    public AudioRing(int capacityFrames, int channels)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames), capacityFrames, "Must be > 0.");
        if (channels <= 0)       throw new ArgumentOutOfRangeException(nameof(channels),       channels,       "Must be > 0.");

        Channels = channels;

        // Round total float slots up to the next power of two.
        int minSlots   = capacityFrames * channels;
        int powerOfTwo = 1;
        while (powerOfTwo < minSlots)
            powerOfTwo <<= 1;

        _totalSlots   = powerOfTwo;
        _mask         = powerOfTwo - 1;
        _buf          = new float[powerOfTwo];
        CapacityFrames = powerOfTwo / channels;
    }

    // ── Producer (MF decode thread) ────────────────────────────────────────────

    /// <summary>
    /// Copies as many floats from <paramref name="samples"/> as the ring can currently hold.
    /// Returns the number of floats actually written (may be less than
    /// <c>samples.Length</c> when near-full; 0 when full).
    /// NEVER blocks. The caller handles backpressure by retrying on the next decode callback.
    /// </summary>
    /// <remarks>
    /// Memory ordering: data is copied into <c>_buf</c> first; <c>_writePos</c> is advanced with
    /// <see cref="Volatile.Write{T}"/> last, so the consumer's
    /// <see cref="Volatile.Read{T}"/> of <c>_writePos</c> implies visibility of the copied samples.
    /// </remarks>
    public int Write(ReadOnlySpan<float> samples)
    {
        // Acquire current read position (consumer-published, release-visible here via volatile read).
        long readPos  = Volatile.Read(ref _readPos);
        long writePos = _writePos; // only the producer writes _writePos; plain read is safe here.

        int free = _totalSlots - (int)(writePos - readPos);
        if (free <= 0) return 0;

        int toWrite = Math.Min(samples.Length, free);
        int writeIdx = (int)(writePos & _mask);
        int firstLen = Math.Min(toWrite, _totalSlots - writeIdx);

        // First contiguous segment.
        samples[..firstLen].CopyTo(_buf.AsSpan(writeIdx, firstLen));

        // Second segment (wrap-around), if any.
        if (firstLen < toWrite)
            samples[firstLen..toWrite].CopyTo(_buf.AsSpan(0, toWrite - firstLen));

        // Publish: advance _writePos AFTER the data copy (release).
        Volatile.Write(ref _writePos, writePos + toWrite);
        return toWrite;
    }

    // ── Consumer (WASAPI render thread) ───────────────────────────────────────

    /// <summary>
    /// Copies up to <c>destination.Length</c> floats out of the ring.
    /// Returns the number of floats actually read (may be less than
    /// <c>destination.Length</c> when under-filled; 0 when empty).
    /// NEVER blocks. The caller pads any unread tail of <paramref name="destination"/> with silence.
    /// </summary>
    /// <remarks>
    /// Memory ordering: <c>_writePos</c> is acquired via <see cref="Volatile.Read{T}"/> before the
    /// copy, ensuring data the producer wrote before its <c>Volatile.Write</c> of <c>_writePos</c>
    /// is visible here. <c>_readPos</c> is advanced with <see cref="Volatile.Write{T}"/> after the
    /// copy, returning the slots to the producer.
    /// </remarks>
    public int Read(Span<float> destination)
    {
        // Acquire current write position (producer-published, release-visible here via volatile read).
        long writePos = Volatile.Read(ref _writePos);
        long readPos  = _readPos; // only the consumer writes _readPos; plain read is safe here.

        int available = (int)(writePos - readPos);
        if (available <= 0) return 0;

        int toRead  = Math.Min(destination.Length, available);
        int readIdx = (int)(readPos & _mask);
        int firstLen = Math.Min(toRead, _totalSlots - readIdx);

        // First contiguous segment.
        _buf.AsSpan(readIdx, firstLen).CopyTo(destination[..firstLen]);

        // Second segment (wrap-around), if any.
        if (firstLen < toRead)
            _buf.AsSpan(0, toRead - firstLen).CopyTo(destination[firstLen..toRead]);

        // Publish: advance _readPos AFTER the data copy (release).
        Volatile.Write(ref _readPos, readPos + toRead);
        return toRead;
    }

    // ── Snapshot properties ────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot count of floats available to read.  May be stale immediately after return — that is
    /// acceptable for SPSC; neither thread relies on this for correctness, only for pacing decisions.
    /// </summary>
    public int AvailableToRead
    {
        get
        {
            long w = Volatile.Read(ref _writePos);
            long r = Volatile.Read(ref _readPos);
            return (int)(w - r);
        }
    }

    /// <summary>
    /// Snapshot count of free float slots available to write.  Subject to the same staleness caveat
    /// as <see cref="AvailableToRead"/>.
    /// </summary>
    public int AvailableToWrite
    {
        get
        {
            long w = Volatile.Read(ref _writePos);
            long r = Volatile.Read(ref _readPos);
            return _totalSlots - (int)(w - r);
        }
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Resets the ring to empty.
    /// <para><b>PRECONDITION:</b> only safe when <em>neither</em> the producer nor the consumer is
    /// actively calling <see cref="Write"/> or <see cref="Read"/>.  Call this during pause or
    /// teardown after both threads are quiesced.</para>
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _writePos, 0L);
        Volatile.Write(ref _readPos,  0L);
    }
}
