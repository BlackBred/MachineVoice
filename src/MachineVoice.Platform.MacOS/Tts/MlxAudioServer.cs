using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MachineVoice.Core;

namespace MachineVoice.Platform.MacOS;

/// <summary>
/// Starts mlx_audio.server when nothing listens on a loopback endpoint and stops it after the idle period,
/// which frees the model's memory. A server that someone else started (or a remote one) is only used.
/// A server left behind by a crashed MachineVoice is recognised by its pid file and taken over.
/// </summary>
public sealed class MlxAudioServer : ISpeechServer
{
    public const string Command = "mlx_audio.server";
    public const string LogFileName = "tts-server.log";
    public const string PidFileName = "tts-server.pid";

    static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    readonly object _gate = new();
    readonly SemaphoreSlim _starting = new(1, 1);
    readonly string _logPath;
    readonly string _pidPath;
    readonly Action<string>? _log;
    Process? _process;
    int _port;
    Timer? _unload;
    long _activity;
    bool _disposed;

    public MlxAudioServer(string rootDirectory, Action<string>? log = null)
    {
        _logPath = Path.Combine(rootDirectory, LogFileName);
        _pidPath = Path.Combine(rootDirectory, PidFileName);
        _log = log;
    }

    /// <summary>The command in the usual install locations; apps started from Finder do not get the shell's PATH.</summary>
    public static string? FindCommand()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/local/bin", "/opt/homebrew/bin", Path.Combine(home, ".local", "bin")]);
        return directories
            .Select(directory => Path.Combine(directory, Command))
            .FirstOrDefault(File.Exists);
    }

    public async Task EnsureRunningAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (!endpoint.IsLoopback)
            return;

        await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var port = endpoint.Port;
            lock (_gate)
            {
                if (_process is { HasExited: true } exited)
                    Forget(exited);
                else if (_process is not null && _port != port)
                    Kill("the endpoint changed");
            }

            if (await IsListeningAsync(port, cancellationToken).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_process is null && Leftover() is { } leftover)
                    {
                        _process = leftover;
                        _port = port;
                        _log?.Invoke($"Took over {Command} left from an earlier run (pid {leftover.Id}).");
                    }
                }

                return;
            }

            lock (_gate)
                Kill("it stopped answering");
            await StartAsync(port, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _starting.Release();
        }
    }

    public void Busy()
    {
        lock (_gate)
        {
            _activity++;
            _unload?.Dispose();
            _unload = null;
        }
    }

    public void Idle(TimeSpan? unloadAfter)
    {
        lock (_gate)
        {
            _activity++;
            _unload?.Dispose();
            _unload = null;
            if (_disposed || _process is null || unloadAfter is not { } delay)
                return;

            var activity = _activity;
            _unload = new Timer(_ => Unload(activity), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _unload?.Dispose();
            _unload = null;
            Kill("MachineVoice is quitting");
        }
    }

    async Task StartAsync(int port, CancellationToken cancellationToken)
    {
        var command = FindCommand() ?? throw new FileNotFoundException(
            $"{Command} is not installed: uv tool install --python 3.12 \"mlx-audio[tts,server]\"");

        var start = new ProcessStartInfo(command)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--host");
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        start.Environment["TQDM_DISABLE"] = "1";

        var output = new StreamWriter(_logPath, append: true) { AutoFlush = true };
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => WriteLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => WriteLine(output, e.Data);
        process.Exited += (_, _) =>
        {
            lock (output)
                output.Dispose();
        };

        try
        {
            process.Start();
        }
        catch
        {
            output.Dispose();
            process.Dispose();
            throw;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        lock (_gate)
        {
            _process = process;
            _port = port;
        }

        File.WriteAllText(_pidPath, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _log?.Invoke($"Started {Command} on port {port} (pid {process.Id}), log: {_logPath}");

        var deadline = DateTime.UtcNow + StartTimeout;
        while (!await IsListeningAsync(port, cancellationToken).ConfigureAwait(false))
        {
            if (process.HasExited)
            {
                var exitCode = process.ExitCode;
                lock (_gate)
                    Forget(process);
                throw new InvalidOperationException($"{Command} exited with code {exitCode}; see {_logPath}.");
            }

            if (DateTime.UtcNow > deadline)
            {
                lock (_gate)
                    Kill("it did not start listening");
                throw new TimeoutException($"{Command} did not start within {StartTimeout.TotalSeconds:0} s; see {_logPath}.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    void Unload(long activity)
    {
        lock (_gate)
        {
            if (_disposed || activity != _activity)
                return;
            _unload?.Dispose();
            _unload = null;
            Kill("it was idle");
        }
    }

    /// <summary>Called under _gate.</summary>
    void Kill(string reason)
    {
        if (_process is not { } process)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                _log?.Invoke($"Stopped {Command} (pid {process.Id}): {reason}.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log?.Invoke($"Stopping {Command} failed: {ex.Message}");
        }

        Forget(process);
    }

    /// <summary>Called under _gate.</summary>
    void Forget(Process process)
    {
        if (_process == process)
            _process = null;
        process.Dispose();
        try
        {
            File.Delete(_pidPath);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The server from the pid file, if that process is still a Python process.</summary>
    Process? Leftover()
    {
        if (!File.Exists(_pidPath) || !int.TryParse(File.ReadAllText(_pidPath).Trim(), out var pid))
            return null;

        try
        {
            var process = Process.GetProcessById(pid);
            if (process.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.StartsWith("mlx_audio", StringComparison.OrdinalIgnoreCase))
                return process;

            process.Dispose();
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    static async Task<bool> IsListeningAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    static void WriteLine(StreamWriter output, string? line)
    {
        if (line is null)
            return;
        lock (output)
        {
            try
            {
                output.WriteLine(line);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
