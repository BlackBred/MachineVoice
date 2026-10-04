using System.Text;
using System.Text.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

static class IngestHttp
{
    const int MaxHeaders = 65_536;

    public static async Task HandleAsync(
        Stream stream,
        byte first,
        Func<string, CancellationToken, Task<IngestResponse>> dispatch,
        CancellationToken cancellationToken)
    {
        var data = new MemoryStream();
        data.WriteByte(first);
        var buf = new byte[4096];
        var headerEnd = -1;
        while (headerEnd < 0 && data.Length < MaxHeaders)
        {
            var n = await stream.ReadAsync(buf, cancellationToken).ConfigureAwait(false);
            if (n == 0)
                break;
            data.Write(buf, 0, n);
            headerEnd = IndexOfHeaderEnd(data);
        }

        if (headerEnd < 0)
        {
            await WriteAsync(stream, 400, IngestResponse.Rejected(ProtocolErrors.InvalidMessage), cancellationToken).ConfigureAwait(false);
            return;
        }

        var headerBytes = data.ToArray();
        var header = Encoding.ASCII.GetString(headerBytes, 0, headerEnd);
        if (!header.StartsWith("POST ", StringComparison.Ordinal))
        {
            await WriteAsync(stream, 400, IngestResponse.Rejected(ProtocolErrors.UnknownCommand), cancellationToken).ConfigureAwait(false);
            return;
        }

        var length = ContentLength(header);
        if (length <= 0 || length > Ndjson.MaxLineChars)
        {
            await WriteAsync(stream, 400, IngestResponse.Rejected(ProtocolErrors.InvalidMessage), cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = new byte[length];
        var already = headerBytes.Length - headerEnd;
        var got = 0;
        if (already > 0)
        {
            got = Math.Min(already, length);
            Buffer.BlockCopy(headerBytes, headerEnd, body, 0, got);
        }

        if (ExpectContinue(header) && got < length)
        {
            var cont = "HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(cont, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        while (got < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(got, length - got), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                break;
            got += n;
        }

        if (got != length)
        {
            await WriteAsync(stream, 400, IngestResponse.Rejected(ProtocolErrors.InvalidMessage), cancellationToken).ConfigureAwait(false);
            return;
        }

        IngestResponse response;
        try
        {
            response = await dispatch(Encoding.UTF8.GetString(body), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = IngestResponse.Rejected(ProtocolErrors.InvalidMessage);
        }

        var status = string.Equals(response.Type, "accepted", StringComparison.Ordinal) ? 200 : 400;
        await WriteAsync(stream, status, response, cancellationToken).ConfigureAwait(false);
    }

    static int IndexOfHeaderEnd(MemoryStream data)
    {
        var buffer = data.GetBuffer();
        var length = (int)data.Length;
        for (var i = 0; i <= length - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i + 4;
        }

        return -1;
    }

    static int ContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = line["Content-Length:".Length..].Trim();
            return int.TryParse(value, out var length) ? length : -1;
        }

        return -1;
    }

    static bool ExpectContinue(string headers) =>
        headers.Contains("Expect: 100-continue", StringComparison.OrdinalIgnoreCase);

    static async Task WriteAsync(Stream stream, int status, IngestResponse response, CancellationToken cancellationToken)
    {
        var reason = status == 200 ? "OK" : "Bad Request";
        var json = JsonSerializer.Serialize(response, IngestJsonContext.Default.IngestResponse);
        var body = Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

sealed class PrefixedReadStream : Stream
{
    readonly Stream _inner;
    readonly byte[] _prefix;
    int _offset;

    public PrefixedReadStream(Stream inner, byte first)
    {
        _inner = inner;
        _prefix = [first];
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var fromPrefix = Drain(buffer);
        if (fromPrefix == buffer.Length)
            return fromPrefix;
        return fromPrefix + _inner.Read(buffer[fromPrefix..]);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var fromPrefix = Drain(buffer.Span);
        if (fromPrefix == buffer.Length)
            return fromPrefix;
        var rest = await _inner.ReadAsync(buffer[fromPrefix..], cancellationToken).ConfigureAwait(false);
        return fromPrefix + rest;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    int Drain(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, _prefix.Length - _offset);
        if (count <= 0)
            return 0;
        _prefix.AsSpan(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }
}
