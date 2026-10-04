using System.Net.Sockets;
using System.Text.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class IngestServer : IAsyncDisposable
{
    readonly SpeechEngine _engine;
    readonly Action<string>? _log;
    readonly CancellationTokenSource _shutdown = new();
    readonly Socket _listen;
    readonly string _path;
    Task? _loop;

    public IngestServer(SpeechEngine engine, string path, Action<string>? log)
    {
        _engine = engine;
        _path = path;
        _log = log;
        _listen = UnixSockets.Listen(path);
    }

    public void Start() => _loop = UnixSockets.AcceptLoopAsync(_listen, _shutdown.Token, HandleAsync, _log);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listen.Dispose();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        TryDelete(_path);
        _shutdown.Dispose();
    }

    async Task HandleAsync(Socket client, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(client, ownsSocket: false);
        var first = new byte[1];
        if (await stream.ReadAsync(first, cancellationToken).ConfigureAwait(false) == 0)
            return;

        // hook.sh uses curl, which speaks HTTP. Other clients send one NDJSON line.
        if (first[0] == (byte)'P')
        {
            await IngestHttp.HandleAsync(stream, first[0], DispatchAsync, cancellationToken).ConfigureAwait(false);
            return;
        }

        var prefixed = new PrefixedReadStream(stream, first[0]);
        using var reader = Ndjson.CreateReader(prefixed);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var response = await DispatchLineAsync(line, cancellationToken).ConfigureAwait(false);
        await WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
    }

    async Task<IngestResponse> DispatchLineAsync(string? line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > Ndjson.MaxLineChars)
            return IngestResponse.Rejected(ProtocolErrors.InvalidMessage);
        try
        {
            return await DispatchAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return IngestResponse.Rejected(ProtocolErrors.InvalidMessage);
        }
    }

    async Task<IngestResponse> DispatchAsync(string json, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("type", out var typeValue)
            && typeValue.ValueKind == JsonValueKind.String
                ? typeValue.GetString()
                : null;
        if (string.Equals(type, "hook", StringComparison.Ordinal))
        {
            var envelope = JsonSerializer.Deserialize(json, IngestJsonContext.Default.HookEnvelope);
            return envelope is null
                ? IngestResponse.Rejected(ProtocolErrors.InvalidMessage)
                : await _engine.SubmitHookAsync(envelope, cancellationToken).ConfigureAwait(false);
        }

        var submit = JsonSerializer.Deserialize(json, IngestJsonContext.Default.StoredSubmit);
        return submit is null
            ? IngestResponse.Rejected(ProtocolErrors.InvalidMessage)
            : await _engine.SubmitAsync(submit, cancellationToken).ConfigureAwait(false);
    }

    static Task WriteAsync(Stream stream, IngestResponse response, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, IngestJsonContext.Default.IngestResponse);
        return Ndjson.WriteLineAsync(stream, json, cancellationToken);
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
