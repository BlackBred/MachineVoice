using System.Net;
using System.Text.Json;
using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class SummaryTests
{
    static readonly SummarySettingsDto Enabled = new()
    {
        Enabled = true,
        Endpoint = "http://llm.test/v1/",
        Model = "small",
        ApiKey = "secret",
        TimeoutSeconds = 10,
    };

    [Fact(Timeout = 20000)]
    public async Task Summary_ReplacesTheBody_AndTheHeadWaitsForIt()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new StubHttpHandler((_, _, _) => answer.Task);
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(summary: Enabled)).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft(
            "cursor", "g", "## Длинный ответ\n\n```cs\ncode();\n```", Topic: "что сломалось?"));
        Assert.Null(tts.UtteranceId);
        var queued = Assert.Single((await client.GetSnapshotAsync()).Snapshot!.Queue);
        Assert.Equal(accepted.Id, queued.Id);
        Assert.Null(queued.Speech);

        answer.SetResult(StubHttpHandler.Chat("<think>план</think>Сломался `src/App/Program.cs`, **починил**."));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor, тема: что сломалось.\nСломался Program.cs, починил.", tts.Text);

        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal("http://llm.test/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("small", json.RootElement.GetProperty("model").GetString());
        var user = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.Contains("code();", user);
        Assert.Contains("что сломалось?", user);

        var current = (await client.GetSnapshotAsync()).Snapshot!.Current!;
        Assert.Equal(tts.Text, current.Speech);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_FallsBackToTheRules()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        });
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Model = "slow",
            TimeoutSeconds = 1,
        })).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "**ответ**"));
        Assert.Null(tts.UtteranceId);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nответ.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task HttpError_FallsBackToTheRules()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(summary: Enabled)).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "ответ"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nответ.", tts.Text);
        Assert.Single(http.Requests);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Disabled_DoesNotCallTheEndpoint()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(StubHttpHandler.Chat("не должно")));
        var host = await TestHost.StartAsync(root.Path, tts, http);

        await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "ответ"));
        Assert.Equal("Cursor.\nответ.", tts.Text);
        Assert.Empty(http.Requests);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Settings_AreValidated_AndSurviveRestart()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using var client = await host.ConnectInProcessAsync();

        var noModel = await client.UpdateSettingsAsync(summary: new SummarySettingsDto { Enabled = true });
        Assert.Equal(ProtocolErrors.InvalidArgument, noModel.Error);
        var badEndpoint = await client.UpdateSettingsAsync(summary: new SummarySettingsDto { Endpoint = "ftp://x" });
        Assert.Equal(ProtocolErrors.InvalidArgument, badEndpoint.Error);
        var badTimeout = await client.UpdateSettingsAsync(summary: new SummarySettingsDto { TimeoutSeconds = 0 });
        Assert.Equal(ProtocolErrors.InvalidArgument, badTimeout.Error);
        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.UpdateSettingsAsync()).Error);

        Assert.True((await client.UpdateSettingsAsync(PlaybackMode.Silent, Enabled)).Ok);
        Assert.True((await client.UpdateSettingsAsync(PlaybackMode.Auto)).Ok);
        await client.DisposeAsync();
        await host.DisposeAsync();

        var restarted = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using var again = await restarted.ConnectInProcessAsync();
        var settings = (await again.GetSettingsAsync()).Settings!;
        Assert.Equal(PlaybackMode.Auto, settings.Mode);
        Assert.True(settings.Summary.Enabled);
        Assert.Equal("http://llm.test/v1/", settings.Summary.Endpoint);
        Assert.Equal("small", settings.Summary.Model);
        Assert.Equal(10, settings.Summary.TimeoutSeconds);
        await restarted.DisposeAsync();
    }
}
