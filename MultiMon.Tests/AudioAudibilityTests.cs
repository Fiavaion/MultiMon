using System;
using System.IO;
using MultiMon.Audio;
using Xunit;

namespace MultiMon.Tests;

/// <summary>
/// Guards <see cref="MfAudioSource.HasAudibleAudio"/>: a video/audio file must only count as "has audio"
/// when there is actual signal, not merely a silent placeholder stream (cameras and NLE exports routinely
/// embed one). Self-contained — synthesizes PCM WAVs in temp so there is no external media dependency.
/// </summary>
public class AudioAudibilityTests
{
    [Fact]
    public void SilentStream_IsNotAudible()
    {
        var path = WriteWav(toneHz: 0);   // all-zero samples = a silent placeholder track
        try { Assert.False(MfAudioSource.HasAudibleAudio(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RealTone_IsAudible()
    {
        var path = WriteWav(toneHz: 440); // a real audible tone
        try { Assert.True(MfAudioSource.HasAudibleAudio(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_IsNotAudible() =>
        Assert.False(MfAudioSource.HasAudibleAudio(Path.Combine(Path.GetTempPath(), "multimon-no-such-file.wav")));

    /// <summary>Writes a 1-second 44.1kHz mono 16-bit PCM WAV. toneHz=0 produces digital silence.</summary>
    private static string WriteWav(int toneHz)
    {
        const int rate = 44100, seconds = 1;
        var samples = rate * seconds;
        var path = Path.Combine(Path.GetTempPath(), $"multimon-audtest-{toneHz}-{Guid.NewGuid():N}.wav");

        using var w = new BinaryWriter(File.Create(path));
        var dataBytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);   // PCM, mono
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); // byteRate, blockAlign, bits
        w.Write("data"u8); w.Write(dataBytes);
        for (var i = 0; i < samples; i++)
        {
            short s = toneHz == 0 ? (short)0
                : (short)(Math.Sin(2 * Math.PI * toneHz * i / rate) * 0.5 * short.MaxValue);
            w.Write(s);
        }
        return path;
    }
}
