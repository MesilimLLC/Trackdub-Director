using System.Buffers.Binary;
using Trackdub.Director.Core;

namespace Trackdub.Director.Tests;

public sealed class WavAudioFormatTests
{
    [Theory]
    [InlineData(16000)]
    [InlineData(24000)]
    public void Accepts_mono_pcm16_at_supported_rates(int sampleRate)
    {
        Assert.Null(WavAudioFormat.ValidateForTranscription(CreateWav(sampleRate)));
    }

    [Theory]
    [InlineData(2, 16000, 16)]
    [InlineData(1, 48000, 16)]
    [InlineData(1, 16000, 24)]
    public void Rejects_encoding_that_needs_conversion(int channels, int sampleRate, int bitsPerSample)
    {
        string? error = WavAudioFormat.ValidateForTranscription(CreateWav(sampleRate, channels, bitsPerSample));
        Assert.Contains("mono 16-bit PCM WAV", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_truncated_chunks()
    {
        byte[] wav = CreateWav();
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 100);

        Assert.Contains("truncated", WavAudioFormat.ValidateForTranscription(wav), StringComparison.Ordinal);
    }

    private static byte[] CreateWav(int sampleRate = 16000, int channels = 1, int bitsPerSample = 16)
    {
        int align = channels * bitsPerSample / 8;
        byte[] wav = new byte[44 + align];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), (uint)(sampleRate * align));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), (ushort)bitsPerSample);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)align);
        return wav;
    }
}
