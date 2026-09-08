using AVFoundation;
using AudioToolbox;
using CoreMedia;
using Foundation;
using MultiMon.Core.Diagnostics;

namespace MultiMon.Audio.Mac;

/// <summary>
/// Decodes one file's audio stream to interleaved 32-bit float PCM with an <see cref="AVAssetReader"/> on its
/// OWN thread (off the main and render threads — decode-threading.md), publishing the PCM into an
/// <see cref="AudioRing"/> for the <see cref="CoreAudioOutput"/> render callback. The Mac twin of
/// <c>MultiMon.Audio.MfAudioSource</c>; works for a standalone audio file (.mp3, .wav, .m4a) and for the audio
/// track of a video container (.mp4, .mov) alike.
///
/// <para><b>Format:</b> the reader output is LinearPCM float32, packed and INTERLEAVED, at the track's NATIVE
/// sample rate and channel count (capped to stereo — AVAssetReader downmixes anything wider, and every output
/// path here is stereo). The unit's AUHAL converter handles the device's format, so nothing resamples twice.</para>
///
/// <para><b>Loop:</b> an AVAssetReader cannot rewind, so end-of-stream recreates the reader — the same loop shape
/// <c>VideoToolboxSource</c> uses. That is NOT the forbidden "seek a live decoder to correct drift": drift is
/// corrected on the render side by dropping/inserting frames against the MasterClock.</para>
///
/// <para><b>Autorelease (LESSON-BUG-008):</b> the decode thread wraps each reader (outer) and each sample buffer
/// (inner) iteration in an <see cref="NSAutoreleasePool"/>; without it every CMSampleBuffer would be retained
/// until the thread exits.</para>
/// </summary>
public sealed class AudioFileSource : IDisposable
{
    private readonly ILog _log;
    private readonly AVUrlAsset _asset;
    private readonly AVAssetTrack _track;
    private readonly AudioSettings _outputSettings;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _faulted;
    private bool _started;
    private int _disposed;
    private AudioRing _ring = null!;          // bound in Start, before the decode thread runs
    private AutoResetEvent? _spaceAvailable;  // consumer pulses this when it frees ring space (set in Start)
    private float[] _scratch = Array.Empty<float>();
    private long _framesDecoded;

    public string Id { get; }

    /// <summary>Native decoded sample rate (Hz), discovered from the track; AUHAL converts to the device rate.</summary>
    public int SampleRate { get; }

    /// <summary>Decoded channel count (the track's, capped to stereo).</summary>
    public int Channels { get; }

    /// <summary>The track's stored codec (AAC, MPEGLayer3, LinearPCM …) — logged, never assumed.</summary>
    public AudioFormatType SourceFormat { get; }

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>True once the decode loop ended on an error — the harness fails the run on it.</summary>
    public bool IsFaulted => _faulted;

    /// <summary>Total sample-frames decoded across loops (diagnostic).</summary>
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    /// <summary>Opens the file and reads its audio track's native format. Throws when there is no audio track —
    /// the caller decides whether that is fatal (the harness fails loudly rather than falling back silently).</summary>
    public AudioFileSource(string path, ILog log, string? id = null)
    {
        _log = log;
        Id = id ?? Path.GetFileNameWithoutExtension(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("Audio source file not found.", path);

        _asset = new AVUrlAsset(NSUrl.FromFilename(path));
        try
        {
            var tracks = _asset.GetTracks(AVMediaTypes.Audio);
            if (tracks.Length == 0)
                throw new InvalidDataException($"'{path}' has no audio track.");
            _track = tracks[0];
            for (var i = 1; i < tracks.Length; i++) tracks[i].Dispose();

            var format = NativeFormat(_track)
                ?? throw new InvalidDataException($"'{path}' audio track has no readable format description.");
            SampleRate = (int)Math.Round(format.SampleRate);
            Channels = Math.Clamp(format.ChannelsPerFrame, 1, 2);
            if (SampleRate <= 0)
                throw new InvalidDataException($"'{path}' audio track reports an invalid sample rate ({format.SampleRate}Hz).");
            SourceFormat = format.Format;

            _outputSettings = new AudioSettings
            {
                Format = AudioFormatType.LinearPCM,
                SampleRate = SampleRate,
                NumberChannels = Channels,
                LinearPcmBitDepth = 32,
                LinearPcmFloat = true,
                LinearPcmBigEndian = false,
                LinearPcmNonInterleaved = false,
            };
        }
        catch
        {
            _track?.Dispose();
            _asset.Dispose();
            throw;
        }

        _log.Info("Audio", $"{Id}: audio decode opened, {SourceFormat} -> native {SampleRate}Hz {Channels}ch float PCM, " +
                           $"dur={_track.TimeRange.Duration.Seconds:0.00}s.");
    }

    /// <summary>
    /// True when the file has an audio track that can be read. The harness uses it to decide whether
    /// <c>--audio</c> can take the video's own audio, instead of failing silently.
    /// </summary>
    public static bool HasAudioTrack(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;
        try
        {
            using var pool = new NSAutoreleasePool();
            using var asset = new AVUrlAsset(NSUrl.FromFilename(path));
            var tracks = asset.GetTracks(AVMediaTypes.Audio);
            var found = tracks.Length > 0 && NativeFormat(tracks[0]) is not null;
            foreach (var track in tracks) track.Dispose();
            return found;
        }
        catch
        {
            return false; // unreadable / no audio → treat as "no audio"
        }
    }

    /// <summary>The track's decoded stream format (its first audio format description), or null.</summary>
    private static AudioStreamBasicDescription? NativeFormat(AVAssetTrack track)
    {
        var descriptions = track.FormatDescriptions;
        try
        {
            foreach (var description in descriptions)
                if (description.AudioStreamBasicDescription is { } asbd)
                    return asbd;
            return null;
        }
        finally
        {
            foreach (var description in descriptions) description.Dispose();
        }
    }

    /// <summary>
    /// Bind the output ring + the consumer's space-available signal and start the decode thread. The ring's
    /// channel count must match <see cref="Channels"/>. The signal lets the decoder block (not busy-spin) while
    /// the bounded ring is full, waking only when the render callback drains a slot.
    /// </summary>
    public void Start(AudioRing ring, AutoResetEvent spaceAvailable)
    {
        if (_started)
            throw new InvalidOperationException("Audio source already started.");
        _started = true;
        _ring = ring;
        _spaceAvailable = spaceAvailable;
        _thread = new Thread(DecodeLoop) { Name = $"MultiMon.Audio.Decode.{Id}", IsBackground = true };
        _thread.Start();
        _log.Info("Audio", $"{Id}: audio decode thread started.");
    }

    private void DecodeLoop()
    {
        try
        {
            while (!_stop)
            {
                using var loopPool = new NSAutoreleasePool();
                using var reader = new AVAssetReader(_asset, out var error);
                if (error is not null)
                    throw new InvalidOperationException($"AVAssetReader: {error.LocalizedDescription}");
                using var output = new AVAssetReaderTrackOutput(_track, _outputSettings) { AlwaysCopiesSampleData = false };
                if (!reader.CanAddOutput(output))
                    throw new InvalidOperationException("AVAssetReader rejected the LinearPCM audio output.");
                reader.AddOutput(output);
                if (!reader.StartReading())
                    throw new InvalidOperationException($"AVAssetReader.StartReading failed: {reader.Error?.LocalizedDescription}");

                var framesThisLoop = 0L;
                try
                {
                    while (!_stop)
                    {
                        using var pool = new NSAutoreleasePool(); // sample + block buffers (LESSON-BUG-008)
                        using var sample = output.CopyNextSampleBuffer();
                        if (sample is null)
                        {
                            if (reader.Status == AVAssetReaderStatus.Failed)
                                throw new InvalidOperationException($"AVAssetReader failed mid-clip: {reader.Error?.LocalizedDescription}");
                            break; // end of stream → recreate the reader and loop the clip
                        }
                        if (sample.NumSamples == 0)
                            continue; // a timing-only marker (edit-list gap), not audio
                        framesThisLoop += WriteSample(sample);
                    }
                }
                finally
                {
                    reader.CancelReading(); // a no-op once the reader completed; required before releasing mid-clip
                }

                if (_stop)
                    break;
                if (framesThisLoop == 0)
                {
                    _faulted = true;
                    _log.Error("Audio", $"{Id}: no audio frames before end-of-stream; stopping audio decode.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _faulted = true;
            _log.Error("Audio", $"{Id}: audio decode loop ended on exception: {ex}");
        }
    }

    /// <summary>Copy one sample buffer's float PCM into the ring, retrying on backpressure. Returns frames written.</summary>
    private long WriteSample(CMSampleBuffer sample)
    {
        using var block = sample.GetDataBuffer();
        if (block is null)
            return 0;

        var floatCount = (int)block.DataLength / sizeof(float);
        if (floatCount <= 0)
            return 0;
        if (floatCount > _scratch.Length)
            _scratch = new float[floatCount];

        // CopyDataBytes (not GetDataPointer) so a block buffer that is internally segmented is still read whole.
        unsafe
        {
            fixed (float* destination = _scratch)
            {
                var status = block.CopyDataBytes(0, (nuint)(floatCount * sizeof(float)), (IntPtr)destination);
                if (status != CMBlockBufferError.None)
                    throw new InvalidOperationException($"CMBlockBuffer.CopyDataBytes returned {status}.");
            }
        }

        // Backpressure: the ring is bounded, so when it is full we BLOCK on the consumer's signal until it drains
        // a slot — real-time pacing (the decoder cannot run ahead of playback), the same self-pacing
        // FrameTimeline.Publish does for video. Event-driven (not a sleep-spin) so a primed decoder adds no idle
        // CPU. The 5ms timeout bounds the wait so _stop is observed promptly.
        var written = 0;
        while (written < floatCount && !_stop)
        {
            var n = _ring.Write(_scratch.AsSpan(written, floatCount - written));
            written += n;
            if (n == 0)
                _spaceAvailable!.WaitOne(5); // non-null once Start ran (the only path that spins this thread)
        }

        var frames = written / Channels;
        Interlocked.Add(ref _framesDecoded, frames);
        return frames;
    }

    /// <summary>Signal the decode thread to stop and join it (off the main thread — the V0087 rule).</summary>
    public void Stop()
    {
        _stop = true;
        _spaceAvailable?.Set(); // wake the decoder if it is blocked on backpressure, so the join is prompt
        _thread?.Join();
        _thread = null;
    }

    /// <summary>Idempotent: a second Dispose is a no-op.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        _track.Dispose();
        _asset.Dispose();
    }
}
