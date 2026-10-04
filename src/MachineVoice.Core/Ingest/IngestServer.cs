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
        using var reader = Ndjson.CreateReader(stream);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line) || line.Length > Ndjson.MaxLineChars)
        {
            await WriteAsync(stream, IngestResponse.Rejected(ProtocolErrors.InvalidMessage), cancellationToken).ConfigureAwait(false);
            return;
        }

        IngestResponse response;
        try
        {
            var submit = JsonSerializer.Deserialize(line, IngestJsonContext.Default.StoredSubmit);
            response = submit is null
                ? IngestResponse.Rejected(ProtocolErrors.InvalidMessage)
                : await _engine.SubmitAsync(submit, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            response = IngestResponse.Rejected(ProtocolErrors.InvalidMessage);
        }

        await WriteAsync(stream, response, cancellationToken).ConfigureAwait(false);
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
