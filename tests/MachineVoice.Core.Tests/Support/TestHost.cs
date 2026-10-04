using MachineVoice.Core;

namespace MachineVoice.Core.Tests;

sealed class TempRoot : IAsyncDisposable
{
    public TempRoot()
    {
        // macOS sun_path is 104 bytes; the default temp directory is too long for a socket path.
        Path = System.IO.Path.Combine("/tmp", "mv" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

static class TestHost
{
    public static Task<MachineVoiceHost> StartAsync(string root, ITtsEngine tts, HttpMessageHandler? http = null) =>
        MachineVoiceHost.StartAsync(new MachineVoiceOptions
        {
            RootDirectory = root,
            Tts = tts,
            CursorDirectory = Path.Combine(root, "cursor-config"),
            HttpHandler = http,
        });
}
