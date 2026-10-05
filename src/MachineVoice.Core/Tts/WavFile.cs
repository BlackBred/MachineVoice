namespace MachineVoice.Core;

public static class WavFile
{
    /// <summary>Seconds of audio in a RIFF/WAVE file, or null when the header cannot be read.</summary>
    public static double? Seconds(byte[] wav) =>
        Read(wav) is { ByteRate: > 0 } layout ? layout.DataLength / (double)layout.ByteRate : null;

    /// <summary>
    /// The clip with the quiet before the speech cut down to <paramref name="lead"/>, so that clips played one after
    /// another pause evenly. The first <paramref name="skip"/> do not count as speech and are muted when kept: a model
    /// may start a clip with a click. Short fades at both ends keep the joins from clicking. Files other than 16-bit
    /// PCM, and clips without speech, come back unchanged.
    /// </summary>
    public static byte[] TrimStart(byte[] wav, TimeSpan lead, TimeSpan skip)
    {
        if (Read(wav) is not { Format: 1, BitsPerSample: 16, Channels: > 0, SampleRate: > 0 } layout)
            return wav;

        var channels = layout.Channels;
        var samples = new short[layout.DataLength / 2 / channels * channels];
        Buffer.BlockCopy(wav, layout.DataStart, samples, 0, samples.Length * 2);
        var frames = samples.Length / channels;
        var window = Math.Max(layout.SampleRate / 100, 1);

        var loudness = new double[frames / window];
        for (var w = 0; w < loudness.Length; w++)
        {
            double sum = 0;
            for (var i = w * window * channels; i < (w + 1) * window * channels; i++)
                sum += (double)samples[i] * samples[i];
            loudness[w] = Math.Sqrt(sum / (window * channels)) / short.MaxValue;
        }

        if (loudness.Length == 0 || loudness.Max() < 1e-4)
            return wav;

        // About -30 dB under the loudest window: the quiet between clips is far below, soft consonants above.
        var threshold = loudness.Max() * 0.03;
        var skipFrames = (int)(skip.TotalSeconds * layout.SampleRate);
        var onset = Array.FindIndex(loudness, (skipFrames + window - 1) / window, level => level >= threshold);
        if (onset < 0)
            return wav;

        var start = Math.Max(0, onset * window - (int)(lead.TotalSeconds * layout.SampleRate));
        var kept = samples.AsSpan(start * channels).ToArray();
        var keptFrames = kept.Length / channels;
        var muted = Math.Clamp(skipFrames - start, 0, keptFrames);
        kept.AsSpan(0, muted * channels).Clear();
        Fade(kept, channels, muted, Math.Min(layout.SampleRate / 100, keptFrames - muted), fadeIn: true);
        var fadeOut = Math.Min(layout.SampleRate * 15 / 1000, keptFrames - muted);
        Fade(kept, channels, keptFrames - fadeOut, fadeOut, fadeIn: false);
        return Pcm16(kept, channels, layout.SampleRate);
    }

    /// <summary>A 16-bit mono PCM file from samples in -1..1.</summary>
    public static byte[] Mono16(ReadOnlySpan<float> samples, int sampleRate)
    {
        var pcm = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            pcm[i] = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * short.MaxValue);
        return Pcm16(pcm, 1, sampleRate);
    }

    static void Fade(short[] samples, int channels, int from, int length, bool fadeIn)
    {
        for (var frame = 0; frame < length; frame++)
        {
            var gain = fadeIn ? (frame + 1.0) / (length + 1) : (length - frame) / (length + 1.0);
            for (var c = 0; c < channels; c++)
            {
                ref var sample = ref samples[(from + frame) * channels + c];
                sample = (short)Math.Round(sample * gain);
            }
        }
    }

    static byte[] Pcm16(short[] samples, int channels, int sampleRate)
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
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(dataSize);
        }

        Buffer.BlockCopy(samples, 0, bytes, headerSize, dataSize);
        return bytes;
    }

    static Layout? Read(byte[] wav)
    {
        if (wav.Length < 12 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return null;

        Layout? format = null;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = wav.AsSpan(at, 4);
            var size = BitConverter.ToUInt32(wav, at + 4);
            var body = at + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && body + 16 <= wav.Length)
            {
                format = new Layout(
                    BitConverter.ToInt16(wav, body),
                    BitConverter.ToInt16(wav, body + 2),
                    BitConverter.ToInt32(wav, body + 4),
                    BitConverter.ToInt32(wav, body + 8),
                    BitConverter.ToInt16(wav, body + 14),
                    0,
                    0);
            }
            else if (id.SequenceEqual("data"u8))
            {
                // A streamed file may leave the size at its maximum; the bytes that are there count.
                var data = (int)Math.Min(size, (long)wav.Length - body);
                return format is null ? null : format with { DataStart = body, DataLength = data };
            }

            var next = body + (long)size + (size & 1);
            if (next > wav.Length)
                break;
            at = (int)next;
        }

        return null;
    }

    sealed record Layout(int Format, int Channels, int SampleRate, int ByteRate, int BitsPerSample, int DataStart, int DataLength);
}
