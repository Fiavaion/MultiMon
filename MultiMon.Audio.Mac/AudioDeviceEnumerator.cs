using System.Runtime.InteropServices;
using CoreFoundation;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Audio.Mac;

/// <summary>
/// The Mac twin of <c>WasapiOutput.EnumerateDevices</c>: lists the CoreAudio hardware devices that have at
/// least one OUTPUT channel, and resolves a device UID back to the <c>AudioObjectID</c> that
/// <see cref="AudioUnit.AudioUnit.SetCurrentDevice"/> takes.
///
/// <para><b>Id contract:</b> <see cref="AudioOutputDevice.Id"/> is the CoreAudio device UID string
/// (<c>kAudioDevicePropertyDeviceUID</c>) — the stable-across-sessions key, exactly as the Windows side uses the
/// IMMDevice id string. AudioObjectIDs are NOT stable (they are re-issued when a device is re-plugged), so they
/// never leave this class; <see cref="FindByUid"/> maps a stored UID to the current id at open / rebuild time.</para>
///
/// <para><b>Property API only:</b> the .NET macOS bindings mark <c>AudioObjectPropertyAddress</c> and its
/// selector enums INTERNAL and expose no <c>AudioObjectGetPropertyData</c> entry point, so the struct is mirrored
/// (three UInt32s, the native layout) and the three functions are P/Invoked here. Every call is best-effort: a
/// device that fails a property read is logged and skipped, never thrown to the caller.</para>
/// </summary>
public static class AudioDeviceEnumerator
{
    private const string CoreAudioLibrary = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";

    /// <summary>kAudioObjectSystemObject — the well-known id of the audio system object.</summary>
    private const uint SystemObject = 1;

    // CoreAudio four-character-code selectors and scopes (the binding enums that carry them are internal).
    private const uint SelectorDevices = 0x64657623;              // 'dev#'  kAudioHardwarePropertyDevices
    private const uint SelectorDefaultOutputDevice = 0x644F7574;  // 'dOut'  kAudioHardwarePropertyDefaultOutputDevice
    private const uint SelectorDeviceUid = 0x75696420;            // 'uid '  kAudioDevicePropertyDeviceUID
    private const uint SelectorObjectName = 0x6C6E616D;           // 'lnam'  kAudioObjectPropertyName
    private const uint SelectorStreamConfiguration = 0x736C6179;  // 'slay'  kAudioDevicePropertyStreamConfiguration
    private const uint SelectorNominalSampleRate = 0x6E737274;    // 'nsrt'  kAudioDevicePropertyNominalSampleRate
    private const uint SelectorDeviceIsAlive = 0x6C69766E;        // 'livn'  kAudioDevicePropertyDeviceIsAlive

    private const uint ScopeGlobal = 0x676C6F62;                  // 'glob'  kAudioObjectPropertyScopeGlobal
    private const uint ScopeOutput = 0x6F757470;                  // 'outp'  kAudioObjectPropertyScopeOutput
    private const uint ElementMain = 0;                           //         kAudioObjectPropertyElementMain

    /// <summary>Mirror of the native AudioObjectPropertyAddress (three UInt32s, sequential).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyDataSize(uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, out uint dataSize);

    [DllImport(CoreAudioLibrary)]
    private static extern int AudioObjectGetPropertyData(uint objectId, ref AudioObjectPropertyAddress address,
        uint qualifierDataSize, IntPtr qualifierData, ref uint dataSize, IntPtr data);

    [DllImport(CoreAudioLibrary)]
    [return: MarshalAs(UnmanagedType.U1)] // native Boolean (unsigned char), not OSStatus
    private static extern bool AudioObjectHasProperty(uint objectId, ref AudioObjectPropertyAddress address);

    private static AudioObjectPropertyAddress Address(uint selector, uint scope = ScopeGlobal) =>
        new() { Selector = selector, Scope = scope, Element = ElementMain };

    /// <summary>
    /// Every CoreAudio device with output channels, in system order, with the default output flagged.
    /// Best-effort: a device whose UID cannot be read is skipped (it cannot be routed to anyway).
    /// </summary>
    public static IReadOnlyList<AudioOutputDevice> Enumerate(ILog log)
    {
        var devices = new List<AudioOutputDevice>();
        try
        {
            var defaultId = DefaultOutputDeviceId();
            foreach (var id in AllDeviceIds())
            {
                var channels = OutputChannelCount(id);
                if (channels == 0)
                    continue; // input-only device (a microphone) — not a render endpoint

                var uid = DeviceUid(id);
                if (uid is null)
                {
                    log.Error("Audio", $"CoreAudio device {id} has no UID; skipping it.");
                    continue;
                }

                devices.Add(new AudioOutputDevice
                {
                    Id = uid,
                    Name = StringProperty(id, SelectorObjectName) ?? uid,
                    ChannelCount = channels,
                    SampleRate = (int)Math.Round(NominalSampleRate(id)),
                    IsDefault = id == defaultId,
                });
            }
        }
        catch (Exception ex)
        {
            log.Error("Audio", $"audio device enumeration failed: {ex.Message}");
        }
        return devices;
    }

    /// <summary>The current default output device's AudioObjectID, or 0 when there is no audio hardware.</summary>
    public static uint DefaultOutputDeviceId()
    {
        var address = Address(SelectorDefaultOutputDevice);
        return ReadUInt32(SystemObject, ref address);
    }

    /// <summary>The AudioObjectID currently carrying <paramref name="uid"/>, or 0 when that device is gone.</summary>
    public static uint FindByUid(string uid)
    {
        foreach (var id in AllDeviceIds())
            if (DeviceUid(id) == uid)
                return id;
        return 0;
    }

    /// <summary>kAudioDevicePropertyDeviceUID of a device id (the <see cref="AudioOutputDevice.Id"/> key), or null
    /// when unreadable.</summary>
    public static string? DeviceUid(uint deviceId) => StringProperty(deviceId, SelectorDeviceUid);

    /// <summary>kAudioDevicePropertyDeviceIsAlive — false once the endpoint is unplugged or torn down.</summary>
    public static bool IsAlive(uint deviceId)
    {
        if (deviceId == 0)
            return false;
        var address = Address(SelectorDeviceIsAlive);
        // A device that has vanished loses the property entirely; that is "not alive", not an error.
        if (!AudioObjectHasProperty(deviceId, ref address))
            return false;
        return ReadUInt32(deviceId, ref address) != 0;
    }

    /// <summary>Friendly name for a device id, for logs. Falls back to the id when the name is unreadable.</summary>
    public static string DescribeDevice(uint deviceId) =>
        StringProperty(deviceId, SelectorObjectName) ?? $"device {deviceId}";

    private static uint[] AllDeviceIds()
    {
        var address = Address(SelectorDevices);
        if (AudioObjectGetPropertyDataSize(SystemObject, ref address, 0, IntPtr.Zero, out var byteSize) != 0 || byteSize == 0)
            return Array.Empty<uint>();

        var count = (int)(byteSize / sizeof(uint));
        var ids = new uint[count];
        var buffer = Marshal.AllocHGlobal((int)byteSize);
        try
        {
            var size = byteSize;
            if (AudioObjectGetPropertyData(SystemObject, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return Array.Empty<uint>();
            for (var i = 0; i < count; i++)
                ids[i] = (uint)Marshal.ReadInt32(buffer, i * sizeof(uint));
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return ids;
    }

    /// <summary>
    /// Sum of the channels across the device's OUTPUT streams (kAudioDevicePropertyStreamConfiguration returns an
    /// AudioBufferList: a UInt32 count then 16-byte AudioBuffer entries, 8-aligned after the count).
    /// </summary>
    private static int OutputChannelCount(uint deviceId)
    {
        var address = Address(SelectorStreamConfiguration, ScopeOutput);
        if (AudioObjectGetPropertyDataSize(deviceId, ref address, 0, IntPtr.Zero, out var byteSize) != 0 || byteSize < sizeof(uint))
            return 0;

        var buffer = Marshal.AllocHGlobal((int)byteSize);
        try
        {
            var size = byteSize;
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return 0;

            const int bufferListHeader = 8;   // UInt32 mNumberBuffers, then mBuffers[] at the 8-byte alignment
            const int audioBufferStride = 16; // UInt32 mNumberChannels, UInt32 mDataByteSize, void* mData
            var buffers = (uint)Marshal.ReadInt32(buffer, 0);
            var channels = 0;
            for (var i = 0; i < buffers; i++)
            {
                var offset = bufferListHeader + i * audioBufferStride;
                if (offset + sizeof(uint) > (int)size)
                    break;
                channels += Marshal.ReadInt32(buffer, offset);
            }
            return channels;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static double NominalSampleRate(uint deviceId)
    {
        var address = Address(SelectorNominalSampleRate);
        var size = (uint)sizeof(double);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(deviceId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return 0;
            var bits = Marshal.ReadInt64(buffer, 0);
            return BitConverter.Int64BitsToDouble(bits);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint ReadUInt32(uint objectId, ref AudioObjectPropertyAddress address)
    {
        var size = (uint)sizeof(uint);
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return 0;
            return (uint)Marshal.ReadInt32(buffer, 0);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Read a CFStringRef property and release it (the property getter hands back a +1 reference).</summary>
    private static string? StringProperty(uint objectId, uint selector)
    {
        var address = Address(selector);
        var size = (uint)IntPtr.Size;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (AudioObjectGetPropertyData(objectId, ref address, 0, IntPtr.Zero, ref size, buffer) != 0)
                return null;
            var handle = Marshal.ReadIntPtr(buffer, 0);
            if (handle == IntPtr.Zero)
                return null;
            var value = CFString.FromHandle(handle);
            CFString.ReleaseNative(handle);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
