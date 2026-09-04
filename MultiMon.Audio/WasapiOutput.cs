using System.Runtime.InteropServices;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;
using MultiMon.Core.Timing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace MultiMon.Audio;

/// <summary>
/// One shared-mode WASAPI render endpoint and the single thread that feeds it (REBUILD_ARCHITECTURE
/// §2.2 — one WASAPI render thread per output device). The render thread is event-driven: the audio
/// engine signals it each time the device wants more frames; it pulls PCM from an <see cref="AudioRing"/>
/// (written by the decode thread), applies gain, and writes it into the endpoint buffer.
///
/// <para><b>Format:</b> the client is initialised in the DECODER's native float format and WASAPI's
/// <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM</c> resamples / remixes to the device mix format. So the decoder
/// never resamples (the source reader won't) and this class never assumes the device's bit depth.</para>
///
/// <para><b>One owner, one thread (LESSON-ARCH-001):</b> every Core Audio COM object (enumerator, device,
/// <c>IAudioClient</c>, <c>IAudioRenderClient</c>) is created, used, AND released on the render thread,
/// which also owns its COM apartment (MTA). Nothing else touches them — no cross-thread COM, no UI-thread
/// teardown (the V0087 rule). The client is created ONCE and reused for the whole session; entering /
/// leaving perform mode only gates whether the thread renders content or silence.</para>
///
/// <para><b>Clocked to the MasterClock (ADR 0002 D1):</b> WASAPI drains at the device crystal, which we
/// cannot slave to QPC, so A/V alignment is by drift correction: the consumer tracks how much real clip
/// content it has rendered (<c>_contentFrames</c>) → media time, compares to
/// <see cref="MasterClock.CurrentMediaTime"/>, and DROPS ring frames when behind / INSERTS silence when
/// ahead. It never reseeks the decoder (decode-threading.md). While the clock is paused the thread renders
/// silence and does not advance content time, so resume stays aligned.</para>
/// </summary>
public sealed unsafe class WasapiOutput : IDisposable
{
    private static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2; // tell WASAPI to treat the fill as silence
    private const double DriftCorrectThresholdSec = 0.012; // start correcting before drift is audible (~12ms)

    private readonly ILog _log;
    private readonly MasterClock _clock;
    private float _gain;                 // live: mixer volume×mute×solo×master, read per fill (Volatile)
    private float _pan;                  // live: stereo balance -1..+1, applied for 2-channel output
    private readonly AudioRing _ring;
    private readonly AutoResetEvent _spaceAvailable; // pulsed after a Read frees ring space → wakes the decoder

    private readonly Thread _thread;
    private readonly AutoResetEvent _audioEvent = new(false);
    private readonly ManualResetEventSlim _opened = new(false);

    private volatile bool _stop;
    private Exception? _initException;
    private float[] _discard = Array.Empty<float>(); // reusable scratch for dropped (drift) frames
    private readonly string? _targetDeviceId;        // requested endpoint (null = default)

    // Render-thread-owned COM objects + state.
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private uint _bufferFrames;
    private long _contentFrames;       // real clip frames rendered (excludes silence) → media position

    private long _underruns;           // fills that starved post-prime (Interlocked)
    private long _peakDriftMicros;     // max |audio - clock| seen post-prime (Interlocked)

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Fills that starved (ring empty while running) after priming — should stay 0.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Peak |audio − MasterClock| observed while running, in milliseconds (A/V drift gate).</summary>
    public double PeakDriftMs => Interlocked.Read(ref _peakDriftMicros) / 1000.0;

    /// <summary>Live mixer controls (any thread): the render thread reads them per fill via Volatile.</summary>
    public void SetGain(float gain) => Volatile.Write(ref _gain, Math.Clamp(gain, 0f, 1f));
    public void SetPan(float pan) => Volatile.Write(ref _pan, Math.Clamp(pan, -1f, 1f));

    /// <summary>
    /// Enumerate the active WASAPI render endpoints (D-005, for device selection in the control UI). Runs on
    /// a short-lived MTA thread so it is apartment-safe from any caller (e.g. the WPF UI thread). Best-effort:
    /// any per-device failure is logged and that device skipped — never throws to the caller.
    /// </summary>
    public static IReadOnlyList<AudioOutputDevice> EnumerateDevices(ILog log)
    {
        var devices = new List<AudioOutputDevice>();

        var t = new Thread(() =>
        {
            var comInit = false;
            try
            {
                PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
                comInit = true;
                PInvoke.CoCreateInstance(ClsidMMDeviceEnumerator, null, CLSCTX.CLSCTX_ALL, out IMMDeviceEnumerator en);

                string? defaultId = null;
                try
                {
                    en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var def);
                    defaultId = GetDeviceId(def);
                    Marshal.FinalReleaseComObject(def);
                }
                catch { /* no default endpoint (no audio hardware) — still list any present devices */ }

                en.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var collection);
                collection.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var dev);
                    try
                    {
                        var id = GetDeviceId(dev);
                        if (id is null) continue;
                        devices.Add(new AudioOutputDevice
                        {
                            Id = id,
                            Name = GetFriendlyName(dev, log) ?? id,
                            IsDefault = id == defaultId,
                        });
                    }
                    finally { Marshal.FinalReleaseComObject(dev); }
                }
                Marshal.FinalReleaseComObject(collection);
                Marshal.FinalReleaseComObject(en);
            }
            catch (Exception ex)
            {
                log.Error("Audio", $"audio device enumeration failed: {ex.Message}");
            }
            finally
            {
                if (comInit) PInvoke.CoUninitialize();
            }
        }) { Name = "MultiMon.Audio.Enumerate", IsBackground = true };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        return devices;
    }

    private static string? GetDeviceId(IMMDevice device)
    {
        device.GetId(out var id);
        var managed = id.ToString();
        Marshal.FreeCoTaskMem((IntPtr)id.Value); // GetId returns a CoTaskMem string the caller must free
        return string.IsNullOrEmpty(managed) ? null : managed;
    }

    private static string? GetFriendlyName(IMMDevice device, ILog log)
    {
        try
        {
            device.OpenPropertyStore(STGM.STGM_READ, out var store);
            try
            {
                var key = PInvoke.PKEY_Device_FriendlyName;
                store.GetValue(key, out var value);
                // PKEY_Device_FriendlyName is a VT_LPWSTR. Read the wide-string pointer directly from the
                // blittable PROPVARIANT (vt at offset 0; the data union at offset 8 on x64) — CsWin32's
                // PROPVARIANT.ToString() returns the type name, not the value, so we marshal the pointer.
                const ushort VT_LPWSTR = 31;
                string? name = null;
                var p = (byte*)&value;
                if (*(ushort*)p == VT_LPWSTR)
                    name = Marshal.PtrToStringUni(*(IntPtr*)(p + 8));
                PInvoke.PropVariantClear(ref value);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally { Marshal.FinalReleaseComObject(store); }
        }
        catch (Exception ex)
        {
            log.Error("Audio", $"friendly-name read failed for an endpoint: {ex.Message}");
            return null;
        }
    }

    public WasapiOutput(ILog log, MasterClock clock, float gain, int sampleRate, int channels, AudioRing ring,
        AutoResetEvent spaceAvailable, string? deviceId = null)
    {
        _log = log;
        _clock = clock;
        _gain = Math.Clamp(gain, 0f, 1f);
        _ring = ring;
        _spaceAvailable = spaceAvailable;
        _targetDeviceId = deviceId;
        SampleRate = sampleRate;
        Channels = channels;
        _thread = new Thread(ThreadProc) { Name = "MultiMon.Audio.Wasapi", IsBackground = true };
    }

    /// <summary>
    /// Start the render thread, open the endpoint, and block until the client is initialised (or failed).
    /// Returns false on failure — the engine then logs + skips audio, never crashes. On success the render
    /// thread is already running (rendering silence until the MasterClock starts).
    /// </summary>
    public bool Open()
    {
        _thread.Start();
        _opened.Wait();
        if (_initException is not null)
        {
            _log.Error("Audio", $"WASAPI endpoint open failed; audio disabled: {_initException.Message}");
            return false;
        }
        return true;
    }

    private void ThreadProc()
    {
        // This thread owns its COM apartment and every Core Audio object created below.
        var comInitialized = false;
        try
        {
            try
            {
                PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
                comInitialized = true;
                OpenEndpoint();
            }
            catch (Exception ex)
            {
                _initException = ex;
                return;
            }
            finally
            {
                // ALWAYS unblock Open() — success or failure, including if CoInitializeEx itself threw —
                // so the caller can never hang waiting on a thread that died during setup.
                _opened.Set();
            }

            if (_stop) return;
            _client!.Start();
            RenderLoop();
            // _client is null if the loop ended on a failed rebuild (ReleaseClient ran, ActivateClient didn't).
            try { _client?.Stop(); } catch { /* stopping a lost device is fine */ }
        }
        catch (Exception ex)
        {
            _log.Error("Audio", $"WASAPI render thread ended on exception: {ex.Message}");
        }
        finally
        {
            ReleaseCom();
            if (comInitialized)
                PInvoke.CoUninitialize(); // balance CoInitializeEx only if it succeeded
        }
    }

    /// <summary>Open the target (or default) render endpoint and initialise the client. Creates the
    /// enumerator once; <see cref="ActivateClient"/> does the device-bound part so the invalidation rebuild
    /// can re-run it without re-creating the enumerator.</summary>
    private void OpenEndpoint()
    {
        PInvoke.CoCreateInstance(ClsidMMDeviceEnumerator, null, CLSCTX.CLSCTX_ALL, out _enumerator);
        ActivateClient();
    }

    /// <summary>
    /// Resolves the endpoint (the requested device id, else the default) and builds the shared-mode,
    /// event-driven, auto-converting client + render service on it. Called at open AND on the
    /// device-invalidated rebuild path (D-005). Render thread only.
    /// </summary>
    private void ActivateClient()
    {
        _device = ResolveDevice();

        var iidAudioClient = typeof(IAudioClient).GUID;
        _device!.Activate(&iidAudioClient, CLSCTX.CLSCTX_ALL, null, out var clientObj);
        _client = (IAudioClient)clientObj;

        // The decoder's native interleaved-float format; AUTOCONVERTPCM resamples/remixes to the device mix.
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = WAVE_FORMAT_IEEE_FLOAT,
            nChannels = (ushort)Channels,
            nSamplesPerSec = (uint)SampleRate,
            wBitsPerSample = 32,
            nBlockAlign = (ushort)(Channels * 4),
            nAvgBytesPerSec = (uint)(SampleRate * Channels * 4),
            cbSize = 0,
        };

        const long hns50ms = 500_000; // 50 ms shared-mode buffer
        var flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                  | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                  | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        _client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, hns50ms, 0, &fmt, null);

        _client.SetEventHandle((HANDLE)_audioEvent.SafeWaitHandle.DangerousGetHandle());
        _client.GetBufferSize(out _bufferFrames);

        var iidRenderClient = typeof(IAudioRenderClient).GUID;
        _client.GetService(iidRenderClient, out var renderObj);
        _render = (IAudioRenderClient)renderObj;

        _discard = new float[_bufferFrames * Channels];
        _log.Info("Audio", $"WASAPI endpoint '{_targetDeviceId ?? "(default)"}': feeding {SampleRate}Hz {Channels}ch float (auto-converted), buffer={_bufferFrames} frames, gain={_gain:0.00}.");
    }

    /// <summary>Resolve the requested endpoint by id, falling back to the default if it's unspecified or gone.</summary>
    private IMMDevice ResolveDevice()
    {
        if (!string.IsNullOrEmpty(_targetDeviceId))
        {
            try
            {
                _enumerator!.GetDevice(_targetDeviceId, out var dev);
                return dev;
            }
            catch (Exception ex)
            {
                _log.Error("Audio", $"requested audio device '{_targetDeviceId}' unavailable ({ex.Message}); using the default endpoint.");
            }
        }
        _enumerator!.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var def);
        return def;
    }

    private void RenderLoop()
    {
        var primed = false;
        while (!_stop)
        {
            // Event-driven: wake when the device wants more (or every 200ms as a stop check).
            _audioEvent.WaitOne(200);
            if (_stop) break;

            // The WHOLE iteration (padding query + fill) sits inside the try: GetCurrentPadding is the first
            // call to fail with AUDCLNT_E_DEVICE_INVALIDATED after a device pull, and outside the try it
            // killed the render thread before the D-005 rebuild ever ran.
            try
            {
                _client!.GetCurrentPadding(out var padding);
                var framesToWrite = _bufferFrames - padding;
                if (framesToWrite == 0)
                    continue;

                FillBuffer(framesToWrite, ref primed);
            }
            catch (COMException ex)
            {
                // AUDCLNT_E_DEVICE_INVALIDATED (default device switched, endpoint unplugged, format changed)
                // or another device error (D-005). Rebuild the client on THIS render thread — the only owner
                // of these COM objects — and resume; on repeated failure, stop this output cleanly (audio
                // ends, the app and video keep running). Never crash or wedge.
                _log.Error("Audio", $"WASAPI device error 0x{ex.HResult:X8}; rebuilding the endpoint.");
                if (!TryRebuild())
                {
                    if (!_stop)
                        _log.Error("Audio", "WASAPI endpoint rebuild failed after retries; stopping this output (audio ends, app continues).");
                    break;
                }
                primed = false; // re-prime against the rebuilt endpoint before counting underruns
            }
        }
    }

    /// <summary>
    /// Device-invalidated recovery (D-005), render thread ONLY: stop+release the dead client/render/device
    /// (keep the enumerator), re-resolve the endpoint (requested id, else default), re-activate, and Start.
    /// Bounded retries with a short backoff so a flapping device can't spin. Mirrors the graphics
    /// DeviceRemovedHandler approach. Returns true once presenting audio again.
    /// </summary>
    private bool TryRebuild()
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts && !_stop; attempt++)
        {
            try
            {
                ReleaseClient();
                Thread.Sleep(120 * attempt); // let the endpoint settle (device switch / re-enumeration)
                if (_stop) return false;
                ActivateClient();
                _client!.Start();
                // Content position is realigned to the clock by the prime re-baseline in FillBuffer once the
                // rebuilt endpoint re-primes (the caller sets primed=false), so we don't seek the decoder.
                _log.Info("Audio", $"WASAPI endpoint rebuilt (attempt {attempt}); audio resumed.");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error("Audio", $"WASAPI rebuild attempt {attempt}/{maxAttempts} failed: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>Release the device-bound COM (client, render service, device) — NOT the enumerator, which the
    /// rebuild reuses. Render thread only.</summary>
    private void ReleaseClient()
    {
        try { _client?.Stop(); } catch { /* stopping a lost device is expected to fail */ }
        if (_render is not null) { Marshal.FinalReleaseComObject(_render); _render = null; }
        if (_client is not null) { Marshal.FinalReleaseComObject(_client); _client = null; }
        if (_device is not null) { Marshal.FinalReleaseComObject(_device); _device = null; }
    }

    private void FillBuffer(uint framesToWrite, ref bool primed)
    {
        var running = _clock.IsRunning;

        // Paused (between perform cycles): render silence, hold content position so resume stays aligned.
        if (!running)
        {
            RenderSilence(framesToWrite);
            return;
        }

        // Prime: don't begin consuming (and don't count underruns) until the decoder has filled the ring,
        // so the very first fills after start don't register a spurious startup starvation.
        if (!primed)
        {
            if (_ring.AvailableToRead < (int)framesToWrite * Channels)
            {
                RenderSilence(framesToWrite);
                return;
            }
            // Re-baseline the content position to the clock at the instant we start consuming. The clock has
            // been running since EnterPerform (and through the ~50-80ms ring prime), so anchoring the first
            // content sample to it means drift starts at ~0 and we never "catch up" by dropping a chunk of
            // audio at t=0 (the garbled-start chop). Also covers re-priming after a device rebuild, where the
            // clock is mid-performance: without this the old _contentFrames=0 would drop seconds of audio.
            _contentFrames = (long)(_clock.CurrentMediaTime.TotalSeconds * SampleRate);
            primed = true;
        }

        // Drift = how far the audio's content position is ahead(+)/behind(-) the MasterClock.
        var audioSec = _contentFrames / (double)SampleRate;
        var clockSec = _clock.CurrentMediaTime.TotalSeconds;
        var driftSec = audioSec - clockSec;
        var absMicros = (long)(Math.Abs(driftSec) * 1_000_000);
        if (absMicros > Interlocked.Read(ref _peakDriftMicros))
            Interlocked.Exchange(ref _peakDriftMicros, absMicros);

        // Proportional, ONE-SHOT correction — close exactly the drift, never overshoot (a full-buffer
        // insert/drop oscillates and chops). Both branches are capped to one fill so a correction is bounded.
        var silenceFrames = 0;
        if (driftSec > DriftCorrectThresholdSec)
        {
            // Audio AHEAD: prepend exactly the surplus as silence; the clock catches up by that much.
            silenceFrames = Math.Min((int)(driftSec * SampleRate), (int)framesToWrite);
        }
        else if (driftSec < -DriftCorrectThresholdSec)
        {
            // Audio BEHIND: drop exactly the deficit so content catches up, then fill normally.
            var dropFrames = Math.Min((int)(-driftSec * SampleRate), (int)_bufferFrames);
            var dropped = _ring.Read(_discard.AsSpan(0, dropFrames * Channels));
            if (dropped > 0)
            {
                _contentFrames += dropped / Channels;
                _spaceAvailable.Set();
            }
        }

        // Fill: optional silence prefix (the surplus), then content for the remainder, gain-scaled.
        // GetBuffer/ReleaseBuffer MUST be paired (try/finally) — an unreleased buffer locks the endpoint
        // and wedges every later fill.
        byte* p;
        _render!.GetBuffer(framesToWrite, &p);
        var read = 0;
        try
        {
            var dst = new Span<float>(p, (int)framesToWrite * Channels);
            var contentStart = silenceFrames * Channels;
            if (contentStart > 0)
                dst[..contentStart].Clear();

            var content = dst[contentStart..];
            read = _ring.Read(content);
            if (read > 0)
                ApplyGainAndPan(content[..read]);
            if (read < content.Length)
            {
                content[read..].Clear();
                Interlocked.Increment(ref _underruns); // genuine starvation of the content portion
            }
        }
        finally
        {
            _render.ReleaseBuffer(framesToWrite, 0);
        }
        _contentFrames += read / Channels;          // inserted silence is NOT counted → clock catches up
        if (read > 0)
            _spaceAvailable.Set();                   // freed ring space → wake the decoder
    }

    /// <summary>Apply the live mixer gain (and stereo pan for 2-channel output) to a span of interleaved
    /// float samples. Reads the volatile controls once so a mid-fill change can't tear within the buffer.</summary>
    private void ApplyGainAndPan(Span<float> samples)
    {
        var gain = Volatile.Read(ref _gain);
        var pan = Volatile.Read(ref _pan);

        if (Channels == 2 && pan != 0f)
        {
            // Constant-ish balance: attenuate the opposite channel as pan moves off-centre.
            var left = gain * (pan > 0f ? 1f - pan : 1f);
            var right = gain * (pan < 0f ? 1f + pan : 1f);
            for (var i = 0; i + 1 < samples.Length; i += 2)
            {
                samples[i] *= left;
                samples[i + 1] *= right;
            }
            return;
        }

        if (gain != 1f)
            for (var i = 0; i < samples.Length; i++)
                samples[i] *= gain;
    }

    /// <summary>Write a fill of silence (driver zeroes it via the SILENT flag); does not advance content time.</summary>
    private void RenderSilence(uint frames)
    {
        // No code between GetBuffer and ReleaseBuffer (the SILENT flag tells the driver to zero-fill), so
        // the pair can't be split by a throw — no try/finally needed here, unlike the content fill.
        byte* p;
        _render!.GetBuffer(frames, &p);
        _render.ReleaseBuffer(frames, AUDCLNT_BUFFERFLAGS_SILENT);
    }

    private void ReleaseCom()
    {
        if (_render is not null) { Marshal.FinalReleaseComObject(_render); _render = null; }
        if (_client is not null) { Marshal.FinalReleaseComObject(_client); _client = null; }
        if (_device is not null) { Marshal.FinalReleaseComObject(_device); _device = null; }
        if (_enumerator is not null) { Marshal.FinalReleaseComObject(_enumerator); _enumerator = null; }
    }

    /// <summary>Signal the render thread to stop and join it (off the UI thread). Idempotent.</summary>
    public void Stop()
    {
        _stop = true;
        _audioEvent.Set(); // wake the render loop out of its event wait
        if (_thread.IsAlive)
            _thread.Join();
    }

    public void Dispose()
    {
        Stop();
        _audioEvent.Dispose();
        _opened.Dispose();
    }
}
