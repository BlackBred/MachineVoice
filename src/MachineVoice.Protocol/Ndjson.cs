using System.Text;

namespace MachineVoice.Protocol;

public static class Ndjson
{
    public const int MaxLineChars = 2 * 1024 * 1024;

    public static StreamReader CreateReader(Stream stream) =>
        new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 16 * 1024, leaveOpen: true);

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken cancellationToken)
    {
        if (line.Length > MaxLineChars)
            throw new InvalidDataException("line too long");

        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
