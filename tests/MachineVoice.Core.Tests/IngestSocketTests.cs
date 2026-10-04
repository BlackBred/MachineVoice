using System.Net.Sockets;
using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class IngestSocketTests
{
    [Fact(Timeout = 20000)]
    public async Task Sockets_ArePrivateAndSeparate()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());

        Assert.NotEqual(host.IngestSocketPath, host.ControlSocketPath);
        Assert.Equal(PrivateFile, Mode(host.IngestSocketPath));
        Assert.Equal(PrivateFile, Mode(host.ControlSocketPath));
        Assert.Equal(PrivateDirectory, Mode(root.Path));
        Assert.Equal(PrivateDirectory, Mode(Path.Combine(root.Path, "inbox")));

        var rejected = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "", "text"));
        Assert.Equal("rejected", rejected.Type);
        Assert.Equal(ProtocolErrors.InvalidArgument, rejected.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(root.Path, "inbox")));

        var controlOnIngest = await RoundTripAsync(host.IngestSocketPath, """{"version":1,"type":"pause","id":"x"}""");
        Assert.Contains("\"type\":\"rejected\"", controlOnIngest);
        Assert.Contains(ProtocolErrors.UnknownCommand, controlOnIngest);

        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task AcceptedItem_IsOnDiskBeforeAck_AndPrivate()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "hello"));
        Assert.False(accepted.Duplicate);

        var files = Directory.GetFiles(Path.Combine(root.Path, "inbox"));
        var file = Assert.Single(files);
        Assert.Equal(PrivateFile, Mode(file));
        var body = await File.ReadAllTextAsync(file);
        Assert.Contains("\"generationId\":\"g\"", body);
        Assert.Contains(accepted.Id!, body);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task ControlSocket_RejectsUnknownCommand()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        var response = await RoundTripAsync(host.ControlSocketPath, """{"version":2,"type":"pause","id":"abc"}""", readGreeting: true);
        Assert.Contains("\"ok\":false", response);
        Assert.Contains(ProtocolErrors.UnsupportedVersion, response);
        Assert.Contains("\"id\":\"abc\"", response);
        await host.DisposeAsync();
    }

    static async Task<string> RoundTripAsync(string path, string line, bool readGreeting = false)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = Ndjson.CreateReader(stream);
        if (readGreeting)
            Assert.False(string.IsNullOrEmpty(await reader.ReadLineAsync()));

        await Ndjson.WriteLineAsync(stream, line, CancellationToken.None);
        var response = await reader.ReadLineAsync();
        Assert.False(string.IsNullOrEmpty(response));
        return response!;
    }

    static UnixFileMode Mode(string path)
    {
        const UnixFileMode mask = (UnixFileMode)0x1FF;
        if (OperatingSystem.IsWindows())
            return default;
        return File.GetUnixFileMode(path) & mask;
    }

    static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    static readonly UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
}
