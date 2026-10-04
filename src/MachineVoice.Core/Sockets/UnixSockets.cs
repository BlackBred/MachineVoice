using System.Net.Sockets;

namespace MachineVoice.Core;

static class UnixSockets
{
    public static Socket Listen(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            if (IsLive(path))
                throw new InvalidOperationException($"Socket is already in use: {path}");
            File.Delete(path);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new UnixDomainSocketEndPoint(path));
            AppLayout.SetPrivateFile(path);
            socket.Listen(16);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static async Task AcceptLoopAsync(
        Socket listen,
        CancellationToken cancellationToken,
        Func<Socket, CancellationToken, Task> handle,
        Action<string>? log)
    {
        var pending = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listen.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var task = HandleAsync(client);
                lock (pending)
                {
                    pending.RemoveAll(static item => item.IsCompleted);
                    pending.Add(task);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!cancellationToken.IsCancellationRequested)
                log?.Invoke($"Socket accept ended: {ex.Message}");
        }
        finally
        {
            Task[] snapshot;
            lock (pending)
                snapshot = pending.ToArray();
            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        async Task HandleAsync(Socket client)
        {
            try
            {
                await handle(client, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or InvalidDataException)
            {
            }
            catch (Exception ex)
            {
                log?.Invoke(ex.ToString());
            }
            finally
            {
                try
                {
                    client.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    static bool IsLive(string path)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
