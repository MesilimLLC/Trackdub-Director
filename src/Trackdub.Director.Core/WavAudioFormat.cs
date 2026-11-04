using System.Buffers.Binary;

namespace Trackdub.Director.Core;

/// <summary>Validates the PCM WAV encoding accepted by the hosted Meta ASR endpoint.</summary>
public static class WavAudioFormat
{
    public static string? ValidateForTranscription(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12)
        {
            return "Audio must be a valid RIFF/WAVE file.";
        }

        if (!HasTag(wav, 0, "RIFF"))
        {
            return "Audio must be a valid RIFF/WAVE file.";
        }

        if (!HasTag(wav, 8, "WAVE"))
        {
            return "Audio must be a valid RIFF/WAVE file.";
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(wav[4..8]);
        if (riffSize < 4)
        {
            return "WAV file is truncated or has an invalid RIFF size.";
        }

        if (riffSize > wav.Length - 8)
        {
            return "WAV file is truncated or has an invalid RIFF size.";
        }

        int riffEnd = checked((int)riffSize + 8);
        string? chunkError = ReadChunks(wav, riffEnd, out WavFields fields);
        return chunkError ?? ValidateHeader(fields);
    }

    private static string? ReadChunks(ReadOnlySpan<byte> wav, int riffEnd, out WavFields fields)
    {
        fields = default;
        for (int offset = 12; offset + 8 <= riffEnd;)
        {
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..(offset + 8)]);
            long chunkEnd = (long)offset + 8 + chunkSize;
            if (chunkEnd > riffEnd)
            {
                return "WAV file contains a truncated chunk.";
            }

            string? chunkError = ReadChunk(wav, offset, chunkSize, ref fields);
            if (chunkError is not null)
            {
                return chunkError;
            }

            long next = chunkEnd + (chunkSize & 1);
            if (next > int.MaxValue)
            {
                return "WAV file contains an invalid chunk size.";
            }

            offset = (int)next;
        }

        return null;
    }

    private static string? ReadChunk(
        ReadOnlySpan<byte> wav,
        int offset,
        uint chunkSize,
        ref WavFields fields)
    {
        int chunkEnd = (int)((long)offset + 8 + chunkSize);
        ReadOnlySpan<byte> chunk = wav[(offset + 8)..chunkEnd];
        if (HasTag(wav, offset, "fmt "))
        {
            if (chunk.Length < 16)
            {
                return "WAV format chunk is incomplete.";
            }

            fields.Encoding = BinaryPrimitives.ReadUInt16LittleEndian(chunk[..2]);
            fields.Channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..4]);
            fields.SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            fields.BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..14]);
            fields.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..16]);
            return null;
        }

        if (HasTag(wav, offset, "data"))
        {
            fields.DataSize = chunkSize;
        }

        return null;
    }

    private static string? ValidateHeader(WavFields fields)
    {
        if (!IsComplete(fields))
        {
            return "WAV must contain a format chunk and non-empty audio data.";
        }

        if (!IsSupportedFormat(fields))
        {
            return "Meta transcription requires mono 16-bit PCM WAV at 16000 or 24000 Hz. Choose a supported WAV or use browser conversion.";
        }

        if (fields.BlockAlign != 2)
        {
            return "WAV audio data does not match its PCM format header.";
        }

        if (fields.DataSize % fields.BlockAlign != 0)
        {
            return "WAV audio data does not match its PCM format header.";
        }

        return null;
    }

    private static bool IsComplete(WavFields fields) =>
        fields is
        {
            Encoding: not null,
            Channels: not null,
            SampleRate: not null,
            BlockAlign: not null,
            BitsPerSample: not null,
            DataSize: not null,
        } && fields.DataSize > 0;

    private static bool IsSupportedFormat(WavFields fields) =>
        fields is { Encoding: 1, Channels: 1, BitsPerSample: 16, SampleRate: 16000 or 24000 };

    private static bool HasTag(ReadOnlySpan<byte> bytes, int offset, string tag)
    {
        return bytes.Length >= offset + tag.Length
            && bytes.Slice(offset, tag.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(tag));
    }

    private struct WavFields
    {
        public ushort? Encoding;
        public ushort? Channels;
        public uint? SampleRate;
        public ushort? BlockAlign;
        public ushort? BitsPerSample;
        public uint? DataSize;
    }
}
