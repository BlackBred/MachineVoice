using System.Net.Sockets;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class ControlServer : IAsyncDisposable
{
    readonly SpeechEngine _engine;
    readonly Action<string>? _log;
    readonly CancellationTokenSource _shutdown = new();
    readonly Socket _listen;
    readonly string _path;
    Task? _loop;

    public ControlServer(SpeechEngine engine, string path, Action<string>? log)
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

        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (Exception)
        {
        }

        _shutdown.Dispose();
    }

    async Task HandleAsync(Socket client, CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(client, ownsSocket: false);
        var slot = new ClientSlot();
        var writer = WriteLoopAsync(stream, slot, cancellationToken);
        await _engine.AttachAsync(slot, cancellationToken).ConfigureAwait(false);
        try
        {
            using var reader = Ndjson.CreateReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;

                ResultMessage result;
                if (line.Length > Ndjson.MaxLineChars)
                {
                    result = Reject("", ProtocolErrors.InvalidMessage);
                }
                else
                {
                    var decoded = ProtocolCodec.ReadClient(line);
                    result = decoded.Message is null
                        ? Reject(decoded.Id ?? "", decoded.Error ?? ProtocolErrors.InvalidMessage)
                        : await _engine.ExecuteAsync(decoded.Message, cancellationToken).ConfigureAwait(false);
                }

                await slot.Outbound.Writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await _engine.DetachAsync(slot, CancellationToken.None).ConfigureAwait(false);
            slot.Outbound.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
            {
            }
        }
    }

    static async Task WriteLoopAsync(Stream stream, ClientSlot slot, CancellationToken cancellationToken)
    {
        await foreach (var message in slot.Outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = ProtocolCodec.Write(message);
            await Ndjson.WriteLineAsync(stream, json, cancellationToken).ConfigureAwait(false);
        }
    }

    static ResultMessage Reject(string id, string error) => new()
    {
        Id = id,
        Ok = false,
        Error = error,
    };
}
