using MultiMon.Audio;

namespace MultiMon.Core.Tests;

/// <summary>
/// Contract tests for <see cref="AudioRing"/> (M6 audio milestone). Portable: the ring lives in
/// MultiMon.Core, so these run on Windows and macOS from the same suite.
/// Covers round-trip correctness, wrap-around integrity, partial I/O accounting, capacity
/// reporting, constructor validation, and a concurrent SPSC soak that verifies the
/// monotonic ordering guarantee with no gaps, duplicates, or torn reads.
/// </summary>
public class AudioRingTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Build a float array whose values are 0f, 1f, 2f … (n-1)f for deterministic checks.</summary>
    private static float[] Ramp(int count, float start = 0f)
    {
        var buf = new float[count];
        for (var i = 0; i < count; i++)
            buf[i] = start + i;
        return buf;
    }

    // ── 1. Write → Read round-trip ─────────────────────────────────────────────

    [Fact]
    public void WriteRead_RoundTrip_ReturnsSamplesInOrder()
    {
        var ring    = new AudioRing(capacityFrames: 64, channels: 2);
        var source  = Ramp(32);        // 16 frames × 2 ch
        var dest    = new float[32];

        int written = ring.Write(source);
        int read    = ring.Read(dest);

        Assert.Equal(32, written);
        Assert.Equal(32, read);
        Assert.Equal(source, dest);
    }

    // ── 2. Wrap-around data integrity ─────────────────────────────────────────

    [Fact]
    public void WrapAround_DataIntegrityHolds()
    {
        // capacity = 16 frames × 2 ch = 32 slots (power-of-two: 32).
        var ring = new AudioRing(capacityFrames: 16, channels: 2);
        int cap  = ring.CapacityFrames * ring.Channels; // total float slots

        // Fill to capacity.
        var fill = Ramp(cap);
        Assert.Equal(cap, ring.Write(fill));

        // Read half to advance readPos into the middle of the array.
        var half = new float[cap / 2];
        Assert.Equal(cap / 2, ring.Read(half));
        Assert.Equal(fill[..(cap / 2)], half);

        // Write a second ramp that wraps the linear end.
        var second = Ramp(cap / 2, start: 100f);
        Assert.Equal(cap / 2, ring.Write(second));

        // Read the remaining first-ramp tail, then the wrapped second ramp.
        var tail   = new float[cap / 2];
        var wraped = new float[cap / 2];
        Assert.Equal(cap / 2, ring.Read(tail));
        Assert.Equal(cap / 2, ring.Read(wraped));

        Assert.Equal(fill[(cap / 2)..], tail);
        Assert.Equal(second, wraped);
    }

    // ── 3. Partial read when under-filled ─────────────────────────────────────

    [Fact]
    public void PartialRead_UnderFilled_ReturnsOnlyAvailable()
    {
        var ring = new AudioRing(capacityFrames: 64, channels: 1);
        var src  = Ramp(10);
        ring.Write(src);

        var dest = new float[50]; // ask for more than available
        int read = ring.Read(dest);

        Assert.Equal(10, read);
        Assert.Equal(src, dest[..10]);
        Assert.Equal(0, ring.AvailableToRead);
    }

    // ── 4. Write returns 0 / partial when full; accounting is exact ───────────

    [Fact]
    public void WriteWhenFull_ReturnsZero()
    {
        var ring = new AudioRing(capacityFrames: 8, channels: 1);
        int cap  = ring.CapacityFrames; // actual power-of-two frames

        var full = Ramp(cap);
        int w = ring.Write(full);
        Assert.Equal(cap, w);
        Assert.Equal(0, ring.AvailableToWrite);
        Assert.Equal(cap, ring.AvailableToRead);

        // Ring is full — no more room.
        Assert.Equal(0, ring.Write(new float[] { 9f }));
    }

    [Fact]
    public void WriteNearFull_WritesOnlyFreeSlots()
    {
        var ring = new AudioRing(capacityFrames: 8, channels: 1);
        int cap  = ring.CapacityFrames;

        // Fill all but 3 slots.
        ring.Write(Ramp(cap - 3));

        var overflow = Ramp(10); // more than 3 free slots
        int written  = ring.Write(overflow);

        Assert.Equal(3, written);
        Assert.Equal(0, ring.AvailableToWrite);
        Assert.Equal(cap, ring.AvailableToRead);
    }

    // ── 5. Channels and CapacityFrames reflect constructor args ───────────────

    [Fact]
    public void CapacityFrames_IsPowerOfTwoRoundedUp()
    {
        // 10 frames × 2 ch = 20 slots → next power of two = 32 → CapacityFrames = 16.
        var ring = new AudioRing(capacityFrames: 10, channels: 2);
        Assert.Equal(2, ring.Channels);
        Assert.Equal(16, ring.CapacityFrames); // 32 slots / 2 channels
    }

    [Fact]
    public void CapacityFrames_ExactPowerOfTwo_NoRounding()
    {
        // 16 frames × 2 ch = 32 slots — already a power of two.
        var ring = new AudioRing(capacityFrames: 16, channels: 2);
        Assert.Equal(16, ring.CapacityFrames);
    }

    [Fact]
    public void Channels_StoredCorrectly()
    {
        var ring = new AudioRing(capacityFrames: 8, channels: 6); // 5.1
        Assert.Equal(6, ring.Channels);
    }

    // ── 6. Constructor argument validation ────────────────────────────────────

    [Fact]
    public void Constructor_ZeroCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRing(0, 2));
    }

    [Fact]
    public void Constructor_NegativeCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRing(-1, 2));
    }

    [Fact]
    public void Constructor_ZeroChannels_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRing(16, 0));
    }

    [Fact]
    public void Constructor_NegativeChannels_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioRing(16, -2));
    }

    // ── 7. Concurrent SPSC soak — monotonic sequence, no gaps or duplicates ───

    /// <summary>
    /// The producer writes a monotonically-increasing float ramp (0f, 1f, 2f …) in small chunks,
    /// retrying when the ring is full.  The consumer reads in chunks of a different size, padding
    /// nothing (it only asserts on actually-read samples), retrying when empty.  After the producer
    /// finishes, the consumer drains whatever remains.  The consumer then verifies that every value
    /// it received forms a gapless, duplicate-free sequence from 0f to (totalFloats-1)f — proving
    /// no torn read, no skipped slot, and no reordering across the wrap boundary.
    /// </summary>
    [Fact]
    public async Task ConcurrentSoak_MonotonicSequence_NoGapsOrDuplicates()
    {
        const int TotalFrames    = 200_000;
        const int Channels       = 2;
        const int TotalFloats    = TotalFrames * Channels;
        const int WriteChunkSize = 64;   // floats per producer push
        const int ReadChunkSize  = 97;   // deliberately misaligned from write size

        var ring = new AudioRing(capacityFrames: 512, channels: Channels);

        // Consumer result: append all successfully-read floats here.
        var received = new System.Collections.Generic.List<float>(TotalFloats);
        var readBuf  = new float[ReadChunkSize];

        // ── Producer task ─────────────────────────────────────────────────────
        var producerDone = new System.Threading.ManualResetEventSlim(false);
        var producer = Task.Run(() =>
        {
            int written = 0;
            var writeBuf = new float[WriteChunkSize];
            while (written < TotalFloats)
            {
                int toWrite = Math.Min(WriteChunkSize, TotalFloats - written);
                for (var i = 0; i < toWrite; i++)
                    writeBuf[i] = (float)(written + i);

                int w = ring.Write(writeBuf.AsSpan(0, toWrite));
                written += w;
                if (w == 0)
                    Thread.SpinWait(50); // ring full: spin-yield without sleeping
            }
            producerDone.Set();
        });

        // ── Consumer task ─────────────────────────────────────────────────────
        var consumer = Task.Run(() =>
        {
            while (true)
            {
                int r = ring.Read(readBuf);
                if (r > 0)
                {
                    for (var i = 0; i < r; i++)
                        received.Add(readBuf[i]);
                }
                else
                {
                    // Empty: check if producer is done and ring is drained.
                    if (producerDone.IsSet && ring.AvailableToRead == 0)
                        break;
                    Thread.SpinWait(50);
                }
            }
        });

        // Time-boxed: 30 s is generous — in CI this typically finishes in < 2 s.
        var both = Task.WhenAll(producer, consumer);
        var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(finished == both, "Soak timed out — possible deadlock or spin-stall.");
        await both; // surface any exception from the worker tasks

        // ── Verify the full monotonic sequence ────────────────────────────────
        Assert.Equal(TotalFloats, received.Count);
        for (var i = 0; i < TotalFloats; i++)
            Assert.Equal((float)i, received[i]);
    }
}
