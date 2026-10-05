using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class CursorAdapterTests
{
    [Fact(Timeout = 20000)]
    public async Task Hooks_QueueTheResponseWhenStopCompletes()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);

        var prompted = await IngestClient.SubmitHookAsync(host.IngestSocketPath, Prompt("gen-1", "первая\nстрока"));
        Assert.Equal("accepted", prompted.Type);
        Assert.Null(prompted.Id);
        Assert.Null(tts.UtteranceId);

        var answered = await IngestClient.SubmitHookAsync(host.IngestSocketPath, Response("gen-1", "готовый ответ"));
        Assert.Null(answered.Id);
        Assert.Null(tts.UtteranceId);

        var stopped = await IngestClient.SubmitHookAsync(host.IngestSocketPath, Stop("gen-1", "completed"));
        Assert.False(stopped.Duplicate);
        Assert.Equal("Cursor, проект MachineVoice.\nготовый ответ.", tts.Text);

        await using var client = await host.ConnectInProcessAsync();
        var snapshot = await client.GetSnapshotAsync();
        var current = snapshot.Snapshot!.Current!;
        Assert.Equal(stopped.Id, current.Id);
        Assert.Equal("cursor", current.Source);
        Assert.Equal("MachineVoice", current.Project);
        Assert.Equal("chat-1", current.ConversationId);
        Assert.Equal("первая строка", current.Topic);
        Assert.Equal("gen-1", current.GenerationId);

        var again = await IngestClient.SubmitHookAsync(host.IngestSocketPath, Stop("gen-1", "completed"));
        Assert.True(again.Duplicate);
        Assert.Equal(stopped.Id, again.Id);
        Assert.Empty(snapshot.Snapshot.Queue);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task StopBeforeText_WaitsUntilTheResponseArrives()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Prompt("gen-2", "тема"));
        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Stop("gen-2", "completed"));
        Assert.Null(tts.UtteranceId);

        var answered = await IngestClient.SubmitHookAsync(host.IngestSocketPath, Response("gen-2", "позже"));
        Assert.False(answered.Duplicate);
        Assert.Equal("Cursor, проект MachineVoice.\nпозже.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task AbortedStop_DropsTheTurn()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Prompt("gen-3", "тема"));
        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Response("gen-3", "не читать"));
        var aborted = await IngestClient.SubmitHookAsync(host.IngestSocketPath, Stop("gen-3", "aborted"));
        Assert.Equal("accepted", aborted.Type);
        Assert.Null(tts.UtteranceId);

        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Stop("gen-3", "completed"));
        Assert.Null(tts.UtteranceId);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Installer_PreservesForeignHooks_AndReportsLegacySpeaker()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        var hooks = HooksPath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(hooks)!);
        await File.WriteAllTextAsync(hooks, """
            {
              "version": 1,
              "note": "keep",
              "hooks": {
                "beforeShellExecution": [
                  { "command": "./hooks/audit.sh", "matcher": "curl|wget", "timeout": 30 }
                ],
                "afterAgentResponse": [
                  { "command": "./hooks/cursor-speaker/speak.sh", "timeout": 5 }
                ],
                "stop": [
                  { "command": "./hooks/hook.sh", "timeout": 1 }
                ]
              }
            }
            """);

        await using var client = await host.ConnectInProcessAsync();
        var connected = await client.ConnectSourceAsync("cursor");
        Assert.True(connected.Ok);
        Assert.Equal(SourceConnectionStatus.Connected, connected.Source!.Status);
        Assert.True(connected.Source.LegacySpeaker);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, Mode(Path.Combine(root.Path, "hook.sh")));

        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(hooks)))
        {
            var tree = doc.RootElement;
            Assert.Equal("keep", tree.GetProperty("note").GetString());
            var events = tree.GetProperty("hooks");
            var audit = events.GetProperty("beforeShellExecution")[0];
            Assert.Equal("./hooks/audit.sh", audit.GetProperty("command").GetString());
            Assert.Equal("curl|wget", audit.GetProperty("matcher").GetString());
            Assert.Equal(30, audit.GetProperty("timeout").GetInt32());
            Assert.Equal("./hooks/cursor-speaker/speak.sh", events.GetProperty("afterAgentResponse")[0].GetProperty("command").GetString());
            Assert.Equal("./hooks/hook.sh", events.GetProperty("stop")[0].GetProperty("command").GetString());
            var command = "'" + Path.Combine(root.Path, "hook.sh") + "'";
            Assert.Contains(events.GetProperty("beforeSubmitPrompt").EnumerateArray(), item => item.GetProperty("command").GetString() == command);
            Assert.Contains(events.GetProperty("afterAgentResponse").EnumerateArray(), item => item.GetProperty("command").GetString() == command && item.GetProperty("timeout").GetInt32() == 5);
            Assert.Contains(events.GetProperty("stop").EnumerateArray(), item => item.GetProperty("command").GetString() == command);
        }

        Assert.True(File.Exists(hooks + ".bak"));
        var disconnected = await client.DisconnectSourceAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Disconnected, disconnected.Source!.Status);
        Assert.True(disconnected.Source.LegacySpeaker);
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(hooks)))
        {
            var events = doc.RootElement.GetProperty("hooks");
            Assert.False(events.TryGetProperty("beforeSubmitPrompt", out _));
            Assert.Equal("./hooks/audit.sh", events.GetProperty("beforeShellExecution")[0].GetProperty("command").GetString());
            Assert.Equal("./hooks/cursor-speaker/speak.sh", events.GetProperty("afterAgentResponse")[0].GetProperty("command").GetString());
            Assert.Single(events.GetProperty("afterAgentResponse").EnumerateArray());
            Assert.Equal("./hooks/hook.sh", events.GetProperty("stop")[0].GetProperty("command").GetString());
        }

        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Installer_ReplacesStalePath_AndDeletesAnEmptyFile()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        var hooks = HooksPath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(hooks)!);
        await File.WriteAllTextAsync(hooks, """
            {
              "version": 1,
              "hooks": {
                "beforeSubmitPrompt": [ { "command": "'/old/MachineVoice/hook.sh'" } ],
                "afterAgentResponse": [ { "command": "'/old/MachineVoice/hook.sh'" } ],
                "stop": [ { "command": "'/old/MachineVoice/hook.sh'" } ]
              }
            }
            """);

        await using var client = await host.ConnectInProcessAsync();
        var stale = await client.GetSourceStatusAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Stale, stale.Source!.Status);

        var connected = await client.ConnectSourceAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Connected, connected.Source!.Status);
        var body = await File.ReadAllTextAsync(hooks);
        Assert.DoesNotContain("/old/MachineVoice/hook.sh", body);
        Assert.Contains(Path.Combine(root.Path, "hook.sh"), body);

        var disconnected = await client.DisconnectSourceAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Disconnected, disconnected.Source!.Status);
        Assert.False(File.Exists(hooks));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task CorruptHooksJson_IsLeftUntouched()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        var hooks = HooksPath(root.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(hooks)!);
        await File.WriteAllTextAsync(hooks, "{");

        await using var client = await host.ConnectInProcessAsync();
        var connected = await client.ConnectSourceAsync("cursor");
        Assert.False(connected.Ok);
        Assert.Equal(ProtocolErrors.InvalidState, connected.Error);
        Assert.Equal("{", await File.ReadAllTextAsync(hooks));
        var status = await client.GetSourceStatusAsync("cursor");
        Assert.Equal(SourceConnectionStatus.Stale, status.Source!.Status);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task HookScript_PostsWhenDaemonIsUp_AndReplaysInboxWhenItIsDown()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.ConnectSourceAsync("cursor")).Ok);
        var script = Path.Combine(root.Path, "hook.sh");

        await RunHookAsync(script, Prompt("live", "из хука"));
        await RunHookAsync(script, Response("live", "озвучить"));
        await RunHookAsync(script, Stop("live", "completed"));
        Assert.Equal("Cursor, проект MachineVoice.\nозвучить.", tts.Text);
        var inbox = Directory.GetFiles(Path.Combine(root.Path, "inbox"));
        var stored = Assert.Single(inbox);
        Assert.DoesNotContain("\"type\":\"hook\"", await File.ReadAllTextAsync(stored));
        tts.Complete();
        await log.TakeAsync<HistoryAppendedEvent>();
        Assert.Empty(Directory.GetFiles(Path.Combine(root.Path, "inbox")));
        await client.DisposeAsync();
        await host.DisposeAsync();

        await RunHookAsync(script, Prompt("saved", "после остановки"));
        await RunHookAsync(script, Response("saved", "из журнала"));
        await RunHookAsync(script, Stop("saved", "completed"));
        var pending = Directory.GetFiles(Path.Combine(root.Path, "inbox"), "*.json");
        Assert.Equal(3, pending.Length);
        Assert.All(pending, path => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, Mode(path)));

        var restarted = new ManualTtsEngine();
        var again = await TestHost.StartAsync(root.Path, restarted);
        Assert.Equal("Cursor, проект MachineVoice.\nиз журнала.", restarted.Text);
        await using var after = await again.ConnectInProcessAsync();
        var snapshot = await after.GetSnapshotAsync();
        Assert.Equal("после остановки", snapshot.Snapshot!.Current!.Topic);
        Assert.Equal("MachineVoice", snapshot.Snapshot.Current.Project);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(root.Path, "inbox")), path => File.ReadAllText(path).Contains("\"type\":\"hook\"", StringComparison.Ordinal));
        await again.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task InvalidHookFile_IsQuarantined()
    {
        await using var root = new TempRoot();
        var inbox = Path.Combine(root.Path, "inbox");
        Directory.CreateDirectory(inbox);
        await File.WriteAllTextAsync(Path.Combine(inbox, "000-bad-hook.json"), """{"version":2,"type":"hook","source":"cursor","payload":{}}""");

        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        Assert.Empty(Directory.GetFiles(inbox));
        Assert.Single(Directory.GetFiles(Path.Combine(root.Path, "rejected")));
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task NewPrompt_DropsQueuedAnswersOfThatChat()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var host = await TestHost.StartAsync(root.Path, tts);
        var speaking = await AnswerAsync(host, "gen-1", "chat-1");
        var answered = await AnswerAsync(host, "gen-2", "chat-1");
        var other = await AnswerAsync(host, "gen-3", "chat-2");

        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Prompt("gen-4", "следующий вопрос"));

        await using var client = await host.ConnectInProcessAsync();
        var snapshot = (await client.GetSnapshotAsync()).Snapshot!;
        Assert.Equal(speaking.Id, snapshot.Current!.Id);
        Assert.Equal(other.Id, Assert.Single(snapshot.Queue).Id);
        var dropped = Assert.Single(snapshot.History);
        Assert.Equal(answered.Id, dropped.Item.Id);
        Assert.Equal(SpeechOutcome.Skipped, dropped.Outcome);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(root.Path, "inbox"), "*.json").Length);
        await host.DisposeAsync();
    }

    static async Task<IngestResponse> AnswerAsync(MachineVoiceHost host, string generation, string conversation)
    {
        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Prompt(generation, "тема", conversation));
        await IngestClient.SubmitHookAsync(host.IngestSocketPath, Response(generation, "ответ", conversation));
        return await IngestClient.SubmitHookAsync(host.IngestSocketPath, Stop(generation, "completed", conversation));
    }

    static string Prompt(string generation, string prompt, string conversation = "chat-1") => JsonSerializer.Serialize(new
    {
        hook_event_name = "beforeSubmitPrompt",
        conversation_id = conversation,
        generation_id = generation,
        prompt,
        workspace_roots = new[] { "/Users/me/Sources/GitHub/MachineVoice" },
    });

    static string Response(string generation, string text, string conversation = "chat-1") => JsonSerializer.Serialize(new
    {
        hook_event_name = "afterAgentResponse",
        conversation_id = conversation,
        generation_id = generation,
        text,
        workspace_roots = new[] { "/Users/me/Sources/GitHub/MachineVoice" },
    });

    static string Stop(string generation, string status, string conversation = "chat-1") => JsonSerializer.Serialize(new
    {
        hook_event_name = "stop",
        conversation_id = conversation,
        generation_id = generation,
        status,
        workspace_roots = new[] { "/Users/me/Sources/GitHub/MachineVoice" },
    });

    static string HooksPath(string root) => Path.Combine(root, "cursor-config", "hooks.json");

    static async Task RunHookAsync(string script, string payload)
    {
        var start = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start bash.");
        await process.StandardInput.WriteAsync(payload);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, stderr);
        Assert.Contains("\"continue\":true", stdout);
    }

    static UnixFileMode Mode(string path)
    {
        const UnixFileMode mask = (UnixFileMode)0x1FF;
        if (OperatingSystem.IsWindows())
            return default;
        return File.GetUnixFileMode(path) & mask;
    }
}
