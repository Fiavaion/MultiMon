using MultiMon.Core.Abstractions;
using MultiMon.Core.Diagnostics;
using MultiMon.Graphics;
using Vortice.DXGI;

namespace MultiMon.Decode.Hap;

/// <summary>
/// Decodes a HAP .mov on its OWN thread (off the UI and render threads) and publishes BCn texture
/// bytes into a <see cref="FrameTimeline"/> for the render thread, mirroring <c>MediaFoundationSource</c>.
/// HAP decode is CPU-only (demux → Snappy → concatenate compressed-texture bytes), so this source holds
/// NO D3D11 device state — it is inherently device-removed-safe: the render thread's pass owns the BCn
/// texture and uploads each published frame, and the HapQ YCoCg→RGB conversion happens in the shader.
///
/// The demux + frame index are built ONCE; the thread loops the clip with a monotonic PTS offset so
/// buffered frames share the MasterClock timeline (drift is corrected by frame selection, never seeking).
/// </summary>
public sealed class HapSource : ISource
{
    private readonly ILog _log;
    private readonly string _path;
    private readonly MovHapDemuxer _demux;
    private readonly int _rowPitch;
    private readonly long _loopDurationTicks;

    private FileStream? _file;
    private Thread? _thread;
    private volatile bool _stop;
    private long _ptsTicks;

    /// <summary>The decode→render handoff. Owned here; the render-side pass borrows from it.</summary>
    public FrameTimeline Frames { get; } = new();

    public int Width => _demux.Width;
    public int Height => _demux.Height;

    /// <summary>The DXGI format the pass must create its source texture in (BCn).</summary>
    public Format TextureFormat { get; }

    /// <summary>True for HAP Q (scaled YCoCg-DXT5) — the pass must use the YCoCg→RGB shader.</summary>
    public bool UseYCoCg { get; }

    public string Id { get; }
    public bool IsRunning => _thread is { IsAlive: true };
    public TimeSpan CurrentPts => TimeSpan.FromTicks(Volatile.Read(ref _ptsTicks));

    public HapSource(string path, ILog log, string? id = null)
    {
        _log = log;
        _path = path;
        Id = id ?? Path.GetFileNameWithoutExtension(path);

        if (!File.Exists(path))
            throw new FileNotFoundException("HAP clip not found.", path);

        _demux = MovHapDemuxer.Parse(path);
        if (_demux.Samples.Count == 0)
            throw new InvalidOperationException($"HAP clip '{path}' has no frames.");

        (TextureFormat, var blockBytes) = MapFormat(_demux.DeclaredFormat);
        UseYCoCg = _demux.DeclaredFormat == HapTextureFormat.YCoCgDxt5;
        _rowPitch = ((_demux.Width + 3) / 4) * blockBytes; // BCn: one row of 4x4 blocks
        // Loop on the last sample's end (its PTS + the clip's average frame duration) so PTS stays monotonic.
        _loopDurationTicks = Math.Max(1, _demux.Duration.Ticks);

        _log.Info("Decode", $"{Id}: HAP {_demux.Fourcc} {Width}x{Height}, {_demux.Samples.Count} frames, " +
                            $"format={TextureFormat} ycocg={UseYCoCg} dur={_demux.Duration.TotalSeconds:0.00}s.");
    }

    public void Start()
    {
        if (_thread is not null)
            throw new InvalidOperationException("Source already started.");
        if (_stop)
            throw new InvalidOperationException("Source cannot be restarted after Stop — create a new source.");
        _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _stop = false;
        _thread = new Thread(DecodeLoop) { Name = $"MultiMon.Decode.{Id}", IsBackground = true };
        try
        {
            _thread.Start();
        }
        catch
        {
            _file.Dispose();   // don't strand the open file handle if the thread fails to start (e.g. OOM)
            _file = null;
            _thread = null;
            throw;
        }
        _log.Info("Decode", $"{Id}: HAP decode thread started.");
    }

    public void Stop()
    {
        _stop = true;
        Frames.SignalStop(); // release the decode thread if it is blocked publishing into a full timeline
        var thread = _thread;
        thread?.Join();
        _thread = null;
        _file?.Dispose();
        _file = null;
    }

    private void DecodeLoop()
    {
        try
        {
            long loopBaseTicks = 0;
            var frameBuffer = Array.Empty<byte>();

            while (!_stop)
            {
                foreach (var sample in _demux.Samples)
                {
                    if (_stop) return;

                    if (frameBuffer.Length < sample.Size)
                        frameBuffer = new byte[sample.Size];
                    ReadFrame(sample.FileOffset, frameBuffer, sample.Size);

                    var decoded = HapFrameDecoder.Decode(frameBuffer.AsSpan(0, sample.Size));
                    var globalPts = loopBaseTicks + sample.PtsTicks;
                    Volatile.Write(ref _ptsTicks, globalPts);

                    // Copy the decoded bytes into an owned buffer (frameBuffer is reused next iteration).
                    var frame = new DecodedFrame(decoded.Data, _rowPitch, TimeSpan.FromTicks(globalPts), static () => { });
                    Frames.Publish(frame);
                }
                loopBaseTicks += _loopDurationTicks; // keep PTS ascending across loops (ADR 0002 D1)
            }
        }
        catch (Exception ex)
        {
            _log.Error("Decode", $"{Id}: HAP decode loop ended on exception: {ex}");
        }
    }

    private void ReadFrame(long offset, byte[] buffer, int size)
    {
        _file!.Position = offset;
        var read = 0;
        while (read < size)
        {
            var n = _file.Read(buffer, read, size - read);
            if (n == 0) throw new EndOfStreamException($"{Id}: unexpected EOF reading a HAP frame.");
            read += n;
        }
    }

    /// <summary>HAP texture format → (DXGI BCn format, bytes per 4×4 block).</summary>
    private static (Format format, int blockBytes) MapFormat(HapTextureFormat hap) => hap switch
    {
        HapTextureFormat.RgbDxt1 => (Format.BC1_UNorm, 8),
        HapTextureFormat.RgbaDxt5 => (Format.BC3_UNorm, 16),
        HapTextureFormat.YCoCgDxt5 => (Format.BC3_UNorm, 16),
        HapTextureFormat.RgbaBptc => (Format.BC7_UNorm, 16),
        HapTextureFormat.ARgtc1 => (Format.BC4_UNorm, 8),
        _ => throw new NotSupportedException($"HAP texture format {hap} is not supported.")
    };

    public void Dispose()
    {
        Stop();
        Frames.Dispose();
    }
}
