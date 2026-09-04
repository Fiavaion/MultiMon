using System.Runtime.InteropServices;
using MultiMon.Core.Diagnostics;
using Vortice.MediaFoundation;
using MF = Vortice.MediaFoundation.MediaFactory;

namespace MultiMon.Audio;

/// <summary>
/// Decodes one file's audio stream to interleaved 32-bit float PCM with an <see cref="IMFSourceReader"/>
/// on its OWN thread (off the UI and render threads — decode-threading.md), publishing the PCM into an
/// <see cref="AudioRing"/> for the <see cref="WasapiOutput"/> render thread. The reader is configured to
/// output PCM at the endpoint's mix format (sample rate + channels), so Media Foundation inserts its own
/// decoder + resampler (e.g. 44.1 kHz MP3 → 48 kHz stereo float) and the render side needs no conversion.
///
/// Mirrors <c>MediaFoundationSource</c>'s discipline: built ONCE and reused for the session; loops at
/// end-of-stream by <c>SetCurrentPosition(0)</c> (the supported source-reader loop — NOT the forbidden
/// "seek a live decoder to correct drift"; drift is corrected on the render side by dropping/inserting
/// frames against the MasterClock). Owns a ref-counted MFStartup/MFShutdown so it works standalone in an
/// audio-only run.
/// </summary>
public sealed class MfAudioSource : IDisposable
{
    private static readonly object StartupGate = new();
    private static int _startupCount;

    private readonly ILog _log;
    private readonly string _path;

    private IMFSourceReader? _reader;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _started;
    private int _disposed;                       // Interlocked: Dispose runs its MFShutdown decrement exactly once
    private AudioRing _ring = null!;             // bound in Start, before the decode thread runs
    private AutoResetEvent? _spaceAvailable;     // consumer pulses this when it frees ring space (set in Start)
    private int _channels;
    private float[] _scratch = Array.Empty<float>();
    private long _framesDecoded;

    public string Id { get; }
    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>Native decoded sample rate (Hz) — discovered from the file; WASAPI resamples to the device.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Native decoded channel count — discovered from the file.</summary>
    public int Channels => _channels;

    /// <summary>Total sample-frames decoded across loops (diagnostic).</summary>
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    public MfAudioSource(string path, ILog log, string? id = null)
    {
        _log = log;
        _path = path;
        Id = id ?? Path.GetFileNameWithoutExtension(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("Audio source file not found.", path);

        Startup();
        try
        {
            Configure();
        }
        catch
        {
            // Configure() failed (unreadable file / no audio stream): balance the MFStartup we just took, so
            // a string of failed opens can't leak the ref-count and strand MFShutdown. The object never
            // escapes the throw, so Dispose won't also decrement.
            ShutdownMf();
            throw;
        }
        _log.Info("Audio", $"{Id}: audio decode opened, native {SampleRate}Hz {_channels}ch float PCM.");
    }

    /// <summary>
    /// Probe whether a media file has a decodable audio stream, WITHOUT building a decode pipeline. The
    /// control UI uses this to decide if a loaded video's audio should be auto-added to the mixer.
    /// Best-effort: any failure (no audio stream, unreadable file) returns false rather than throwing.
    /// Ref-counts MFStartup/MFShutdown through the same gate as the instances, so it is safe to call while
    /// audio is playing.
    /// </summary>
    public static bool HasAudioStream(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        var started = false;
        try
        {
            lock (StartupGate) { MF.MFStartup(false).CheckError(); _startupCount++; started = true; }

            using var reader = MF.MFCreateSourceReaderFromURL(path, null);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using var type = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream);
            return type is not null; // a current type on the first audio stream ⇒ there is decodable audio
        }
        catch
        {
            return false; // no audio stream / unreadable → treat as "no audio"
        }
        finally
        {
            if (started)
                lock (StartupGate) { if (_startupCount > 0 && --_startupCount == 0) MF.MFShutdown(); }
        }
    }

    /// <summary>True only if the file has an audio stream that is actually AUDIBLE — not a silent placeholder
    /// track (cameras, screen recorders, and NLE exports routinely embed one even when there's nothing to
    /// hear). Confirms a stream exists, then decodes float PCM and returns true the instant a sample rises
    /// above a near-silence floor; declares "silent" only if the whole scanned window stays below it. Decode
    /// runs faster than realtime and real audio exceeds the floor almost immediately, so a file with sound
    /// returns near-instantly; only a fully-silent file decodes up to the cap. If the stream can't be decoded
    /// to inspect, falls back to stream-presence so real audio is never wrongly hidden.</summary>
    public static bool HasAudibleAudio(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return false;

        const float SilenceFloor = 1e-4f;            // ≈ -80 dBFS; real audio exceeds this, placeholder silence doesn't
        const long MaxScanTicks = 60 * 10_000_000L;  // scan at most 60s of content (100ns units) before declaring silent

        var started = false;
        try
        {
            lock (StartupGate) { MF.MFStartup(false).CheckError(); _startupCount++; started = true; }

            using var reader = MF.MFCreateSourceReaderFromURL(path, null);
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using (var outType = MF.MFCreateMediaType())
            {
                outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
                outType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, outType); // throws if there's no audio stream
            }

            var scratch = Array.Empty<float>();
            while (true)
            {
                var sample = reader.ReadSample(SourceReaderIndex.FirstAudioStream,
                    SourceReaderControlFlag.None, out _, out var flags, out var timestamp);
                if ((flags & SourceReaderFlag.Error) != 0)
                {
                    sample?.Dispose();
                    return true; // can't inspect further but a stream exists — don't hide it
                }
                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    sample?.Dispose();
                    return false; // whole stream scanned, never rose above the floor → silent placeholder
                }
                if (timestamp > MaxScanTicks)
                {
                    sample?.Dispose();
                    return false; // scanned enough silence; treat as inaudible
                }
                if (sample is null)
                    continue;

                var buffer = sample.ConvertToContiguousBuffer();
                try
                {
                    buffer.Lock(out var data, out _, out var byteLength);
                    var floatCount = byteLength / sizeof(float);
                    if (scratch.Length < floatCount)
                        scratch = new float[floatCount];
                    Marshal.Copy(data, scratch, 0, floatCount);
                    buffer.Unlock();
                    for (var i = 0; i < floatCount; i++)
                        if (Math.Abs(scratch[i]) > SilenceFloor)
                            return true;
                }
                finally
                {
                    buffer.Dispose();
                    sample.Dispose();
                }
            }
        }
        catch
        {
            return HasAudioStream(path); // couldn't decode to inspect — fall back to "has a stream" (never hide real audio)
        }
        finally
        {
            if (started)
                lock (StartupGate) { if (_startupCount > 0 && --_startupCount == 0) MF.MFShutdown(); }
        }
    }

    /// <summary>
    /// Builds a source reader that outputs interleaved 32-bit float PCM at the file's NATIVE sample rate
    /// and channel count. We set only the major type + Float subtype (a partial type) — the source reader
    /// inserts the decoder + a float converter but will NOT resample, so we read back the negotiated rate
    /// and channels and let WASAPI's AUTOCONVERTPCM handle the conversion to the device mix.
    /// </summary>
    private void Configure()
    {
        var reader = MF.MFCreateSourceReaderFromURL(_path, null);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);

        using (var outputType = MF.MFCreateMediaType())
        {
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            outputType.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Float);
            reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, outputType);
        }

        using (var actualType = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream))
        {
            SampleRate = (int)MF.MFGetAttributeUInt32(actualType, MediaTypeAttributeKeys.AudioSamplesPerSecond, 0);
            _channels = (int)MF.MFGetAttributeUInt32(actualType, MediaTypeAttributeKeys.AudioNumChannels, 0);
        }
        if (SampleRate <= 0 || _channels <= 0)
            throw new InvalidOperationException($"decoded audio reports invalid format ({SampleRate}Hz {_channels}ch).");

        _reader = reader;
    }

    /// <summary>
    /// Bind the output ring + the consumer's space-available signal and start the decode thread. The
    /// ring's channel count must match <see cref="Channels"/>. The signal lets the decoder block (not
    /// busy-spin) while the bounded ring is full, waking only when the render thread drains a slot.
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
            var framesThisLoop = 0L;
            while (!_stop)
            {
                var sample = _reader!.ReadSample(SourceReaderIndex.FirstAudioStream,
                    SourceReaderControlFlag.None, out _, out var flags, out _);

                if ((flags & SourceReaderFlag.Error) != 0)
                {
                    sample?.Dispose();
                    _log.Error("Audio", $"{Id}: source reader error flag; stopping audio decode.");
                    break;
                }

                if ((flags & SourceReaderFlag.EndOfStream) != 0)
                {
                    sample?.Dispose();
                    if (framesThisLoop == 0)
                    {
                        _log.Error("Audio", $"{Id}: no audio frames before end-of-stream; stopping audio decode.");
                        break;
                    }
                    framesThisLoop = 0;
                    _reader!.SetCurrentPosition(0); // loop the clip (supported source-reader seek)
                    continue;
                }

                if (sample is null)
                    continue;

                framesThisLoop += WriteSample(sample);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Audio", $"{Id}: audio decode loop ended on exception: {ex}");
        }
    }

    /// <summary>Copy a decoded sample's float PCM into the ring, retrying on backpressure. Returns frames written.</summary>
    private long WriteSample(IMFSample sample)
    {
        var buffer = sample.ConvertToContiguousBuffer();
        try
        {
            buffer.Lock(out var data, out _, out var byteLength);
            var floatCount = byteLength / sizeof(float);
            if (floatCount > _scratch.Length)
                _scratch = new float[floatCount];
            Marshal.Copy(data, _scratch, 0, floatCount);
            buffer.Unlock();

            // Backpressure: the ring is bounded, so when it is full we BLOCK on the consumer's signal until
            // it drains a slot — real-time pacing (the decoder cannot run ahead of playback), the same
            // self-pacing FrameTimeline.Publish does for video. Event-driven (not a Sleep-spin) so a primed
            // decoder adds no idle CPU. The 5ms timeout bounds the wait so _stop is observed promptly.
            var written = 0;
            while (written < floatCount && !_stop)
            {
                var n = _ring.Write(_scratch.AsSpan(written, floatCount - written));
                written += n;
                if (n == 0)
                    _spaceAvailable!.WaitOne(5); // non-null once Start ran (the only path that spins this thread)
            }

            var frames = floatCount / _channels;
            Interlocked.Add(ref _framesDecoded, frames);
            return frames;
        }
        finally
        {
            buffer.Dispose();
            sample.Dispose();
        }
    }

    /// <summary>Signal the decode thread to stop and join it (off the UI thread — the V0087 rule).</summary>
    public void Stop()
    {
        _stop = true;
        _spaceAvailable?.Set(); // wake the decoder if it is blocked on backpressure, so the join is prompt
        _thread?.Join();
        _thread = null;
    }

    private void Startup()
    {
        lock (StartupGate)
        {
            MF.MFStartup(false).CheckError();
            _startupCount++;
        }
    }

    /// <summary>Idempotent: a second Dispose is a no-op, so it can never decrement the shared MFStartup
    /// ref-count twice (which would strand a live sibling with MFShutdown already called).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
        _reader?.Dispose();
        _reader = null;
        ShutdownMf();
    }

    /// <summary>Decrement the shared MFStartup ref-count and call MFShutdown when it reaches zero.</summary>
    private static void ShutdownMf()
    {
        lock (StartupGate)
        {
            if (_startupCount > 0 && --_startupCount == 0)
                MF.MFShutdown();
        }
    }
}
