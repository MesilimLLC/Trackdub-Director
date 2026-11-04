using System.Buffers.Binary;

namespace Trackdub.Director.Core;

/// <summary>Validates the PCM WAV encoding accepted by the hosted Meta ASR endpoint.</summary>
public static class WavAudioFormat
{
    public static string? ValidateForTranscription(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !HasTag(wav, 0, "RIFF") || !HasTag(wav, 8, "WAVE"))
        {
            return "Audio must be a valid RIFF/WAVE file.";
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(wav[4..8]);
        if (riffSize < 4 || riffSize > wav.Length - 8)
        {
            return "WAV file is truncated or has an invalid RIFF size.";
        }

        int riffEnd = checked((int)riffSize + 8);
        ushort? encoding = null;
        ushort? channels = null;
        uint? sampleRate = null;
        ushort? blockAlign = null;
        ushort? bitsPerSample = null;
        uint? dataSize = null;

        for (int offset = 12; offset + 8 <= riffEnd;)
        {
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav[(offset + 4)..(offset + 8)]);
            long chunkEnd = (long)offset + 8 + chunkSize;
            if (chunkEnd > riffEnd)
            {
                return "WAV file contains a truncated chunk.";
            }

            ReadOnlySpan<byte> chunk = wav[(offset + 8)..(int)chunkEnd];
            if (HasTag(wav, offset, "fmt "))
            {
                if (chunk.Length < 16)
                {
                    return "WAV format chunk is incomplete.";
                }

                encoding = BinaryPrimitives.ReadUInt16LittleEndian(chunk[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..4]);
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..14]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..16]);
            }
            else if (HasTag(wav, offset, "data"))
            {
                dataSize = chunkSize;
            }

            long next = chunkEnd + (chunkSize & 1);
            if (next > int.MaxValue)
            {
                return "WAV file contains an invalid chunk size.";
            }

            offset = (int)next;
        }

        if (encoding is null || channels is null || sampleRate is null || blockAlign is null || bitsPerSample is null || dataSize is null or 0)
        {
            return "WAV must contain a format chunk and non-empty audio data.";
        }

        if (encoding != 1 || channels != 1 || bitsPerSample != 16 || sampleRate is not (16000 or 24000))
        {
            return "Meta transcription requires mono 16-bit PCM WAV at 16000 or 24000 Hz. Choose a supported WAV or use browser conversion.";
        }

        if (blockAlign != 2 || dataSize % blockAlign != 0)
        {
            return "WAV audio data does not match its PCM format header.";
        }

        return null;
    }

    private static bool HasTag(ReadOnlySpan<byte> bytes, int offset, string tag)
    {
        return bytes.Length >= offset + tag.Length
            && bytes.Slice(offset, tag.Length).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(tag));
    }
}
