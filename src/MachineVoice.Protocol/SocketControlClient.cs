using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MachineVoice.Protocol;

public sealed class SocketControlClient : ControlClient
{
    readonly NetworkStream _stream;
    readonly StreamReader _reader;
    readonly SemaphoreSlim _write = new(1, 1);
    readonly CancellationTokenSource _shutdown = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<ResultMessage>> _pending = new();
    readonly Channel<EventMessage> _events = Channel.CreateUnbounded<EventMessage>();
    readonly Task _readLoop;
    int _disposed;

    SocketControlClient(NetworkStream stream)
    {
        _stream = stream;
        _reader = Ndjson.CreateReader(stream);
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public static async Task<SocketControlClient> ConnectAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            return new SocketControlClient(new NetworkStream(socket, ownsSocket: true));
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public override async IAsyncEnumerable<EventMessage> EventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var message))
                yield return message;
        }
    }

    protected override async Task<ResultMessage> SendAsync(ClientMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var pending = new TaskCompletionSource<ResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(message.Id, pending))
            throw new InvalidOperationException($"Duplicate command id {message.Id}.");

        try
        {
            var json = ProtocolCodec.Write(message);
            await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Ndjson.WriteLineAsync(_stream, json, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            return await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(message.Id, out _);
            throw;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _events.Writer.TryComplete();
        await _stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var pending in _pending.Values)
            pending.TrySetCanceled(_shutdown.Token);

        _reader.Dispose();
        _write.Dispose();
        _shutdown.Dispose();
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                if (line.Length > Ndjson.MaxLineChars)
                    continue;

                ServerMessage? message;
                try
                {
                    message = ProtocolCodec.ReadServer(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }

                switch (message)
                {
                    case ResultMessage result when _pending.TryRemove(result.Id, out var pending):
                        pending.TrySetResult(result);
                        break;
                    case EventMessage ev:
                        _events.Writer.TryWrite(ev);
                        break;
                }
            }
        }
        catch (Exception) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            _events.Writer.TryComplete();
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("Control connection closed."));
        }
    }
}
