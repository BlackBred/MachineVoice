namespace MachineVoice.Core;

public static class WavFile
{
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
