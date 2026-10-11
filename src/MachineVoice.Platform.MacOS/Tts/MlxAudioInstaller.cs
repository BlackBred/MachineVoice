using System.ComponentModel;
using System.Diagnostics;
using MachineVoice.Core;

namespace MachineVoice.Platform.MacOS;

/// <summary>How OmniVoice's server is installed: <c>uv tool install --python 3.12 "mlx-audio[tts,server]"</c>.</summary>
public static class MlxAudioPackage
{
    public const string Uv = "uv";
    public const string Python = "3.12";
    public const string Name = "mlx-audio[tts,server]";

    public const string MissingUv = "uv is not installed: brew install uv";

    public static string InstallCommand => $"uv tool install --python {Python} \"{Name}\"";
}

/// <summary>
/// Installs mlx_audio.server with uv. Apps started from Finder do not have a shell, so uv is looked up in the
/// usual places, and the install runs without one.
/// </summary>
public sealed class MlxAudioInstaller : IMlxAudioInstaller
{
    static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    readonly Action<string>? _log;
    readonly Func<string?> _findServer;
    readonly Func<string?> _findUv;
    readonly SemaphoreSlim _installing = new(1, 1);

    public MlxAudioInstaller(Action<string>? log = null)
        : this(log, MlxAudioServer.FindCommand, static () => MlxAudioServer.FindExecutable(MlxAudioPackage.Uv))
    {
    }

    /// <summary><paramref name="findServer"/> and <paramref name="findUv"/> replace the path search.</summary>
    public MlxAudioInstaller(Action<string>? log, Func<string?> findServer, Func<string?> findUv)
    {
        _log = log;
        _findServer = findServer;
        _findUv = findUv;
    }

    public bool IsInstalled => _findServer() is not null;

    public async Task InstallAsync(CancellationToken cancellationToken)
    {
        await _installing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled)
            {
                _log?.Invoke($"{MlxAudioServer.Command} is already installed.");
                return;
            }

            var uv = _findUv() ?? throw new FileNotFoundException(MlxAudioPackage.MissingUv);
            _log?.Invoke($"Installing {MlxAudioServer.Command}: {MlxAudioPackage.InstallCommand}");
            await RunAsync(uv, cancellationToken).ConfigureAwait(false);
            if (!IsInstalled)
                throw new InvalidOperationException($"{MlxAudioServer.Command} was not found after uv finished.");
            _log?.Invoke($"{MlxAudioServer.Command} is installed.");
        }
        finally
        {
            _installing.Release();
        }
    }

    async Task RunAsync(string uv, CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var start = new ProcessStartInfo(uv)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(home) ? home : Path.GetTempPath(),
        };
        start.ArgumentList.Add("tool");
        start.ArgumentList.Add("install");
        start.ArgumentList.Add("--python");
        start.ArgumentList.Add(MlxAudioPackage.Python);
        start.ArgumentList.Add(MlxAudioPackage.Name);

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"uv did not start: {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallTimeout);
        using var kill = timeout.Token.Register(() => Kill(process));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw new TimeoutException($"uv tool install did not finish within {InstallTimeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode == 0)
            return;

        var detail = Tail(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 500);
        _log?.Invoke($"uv exited with code {process.ExitCode}: {Tail(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr, 2000)}");
        throw new InvalidOperationException(
            detail.Length == 0
                ? $"uv exited with code {process.ExitCode}."
                : $"uv exited with code {process.ExitCode}: {detail}");
    }

    static async Task DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    static string Tail(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[^max..];
    }
}
