namespace MachineVoice.Core;

public static class WavFile
{
    /// <summary>Seconds of audio in a RIFF/WAVE file, or null when the header cannot be read.</summary>
    public static double? Seconds(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return null;

        var byteRate = 0;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = wav.AsSpan(at, 4);
            var size = BitConverter.ToUInt32(wav, at + 4);
            var body = at + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wav.Length)
            {
                byteRate = BitConverter.ToInt32(wav, body + 8);
            }
            else if (id.SequenceEqual("data"u8))
            {
                // A streamed file may leave the size at its maximum; the bytes that are there count.
                var data = Math.Min((long)size, wav.Length - body);
                return byteRate > 0 ? data / (double)byteRate : null;
            }

            var next = body + (long)size + (size & 1);
            if (next > wav.Length)
                break;
            at = (int)next;
        }

        return null;
    }

    /// <summary>A 16-bit mono PCM file from samples in -1..1.</summary>
    public static byte[] Mono16(ReadOnlySpan<float> samples, int sampleRate)
    {
        const int headerSize = 44;
        var dataSize = samples.Length * 2;
        var bytes = new byte[headerSize + dataSize];
        using (var writer = new BinaryWriter(new MemoryStream(bytes)))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + dataSize);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataSize);
            foreach (var sample in samples)
                writer.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
        }

        return bytes;
    }
}
