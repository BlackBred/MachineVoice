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
        Assert.Equal("Cursor.\nСломался Program.cs, починил.", tts.Text);

        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal("http://llm.test/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("small", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(LlmSummarizer.MaxTokens, json.RootElement.GetProperty("max_tokens").GetInt32());
        var system = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(SummarySettingsDto.DefaultSystemPrompt, system);
        Assert.EndsWith("/no_think", system);
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
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Endpoint = Enabled.Endpoint,
            Model = Enabled.Model,
            ApiKey = Enabled.ApiKey,
            TimeoutSeconds = Enabled.TimeoutSeconds,
            SystemPrompt = "Одно предложение.",
        })).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "ответ"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nответ.", tts.Text);
        var (request, body) = Assert.Single(http.Requests);
        Assert.Equal("http://llm.test/v1/chat/completions", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("Одно предложение.", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task TruncatedAnswer_FallsBackToTheRules()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(StubHttpHandler.Chat("Начало пересказа, которое", "length")));
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(summary: Enabled)).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "ответ"));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nответ.", tts.Text);
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
        var longPrompt = await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            SystemPrompt = new string('а', SummarySettingsDto.MaxSystemPrompt + 1),
        });
        Assert.Equal(ProtocolErrors.InvalidArgument, longPrompt.Error);
        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.UpdateSettingsAsync()).Error);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto { Model = "m", SystemPrompt = "   " })).Ok);
        Assert.Equal(SummarySettingsDto.DefaultSystemPrompt, (await client.GetSettingsAsync()).Settings!.Summary.SystemPrompt);

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
        Assert.Equal(SummarySettingsDto.DefaultSystemPrompt, settings.Summary.SystemPrompt);
        await restarted.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task Probe_ReportsWhetherTheEndpointListsTheModel()
    {
        await using var root = new TempRoot();
        var http = new StubHttpHandler((request, _, _) =>
        {
            if (request.Headers.Authorization?.Parameter == "bad")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            if (request.RequestUri!.Host == "down.test")
                throw new HttpRequestException("refused");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"qwen3:8b:latest"}]}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        });
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), http);
        await using var client = await host.ConnectInProcessAsync();

        var listed = await client.ProbeSummaryAsync("http://llm.test/v1/", "qwen3:8b", "secret");
        Assert.True(listed.Ok);
        Assert.Equal(SummaryProbeStatus.Available, listed.SummaryProbe!.Status);
        var (request, _) = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://llm.test/v1/models", request.RequestUri!.ToString());
        Assert.Equal("Bearer secret", request.Headers.Authorization!.ToString());

        var missing = await client.ProbeSummaryAsync("http://llm.test/v1", "other", null);
        Assert.Equal(SummaryProbeStatus.Missing, missing.SummaryProbe!.Status);
        Assert.Null(http.Requests[^1].Request.Headers.Authorization);

        var unnamed = await client.ProbeSummaryAsync("http://llm.test/v1", "  ", null);
        Assert.Equal(SummaryProbeStatus.Reachable, unnamed.SummaryProbe!.Status);

        var denied = await client.ProbeSummaryAsync("http://llm.test/v1", "qwen3:8b", "bad");
        Assert.Equal(SummaryProbeStatus.Unauthorized, denied.SummaryProbe!.Status);

        var down = await client.ProbeSummaryAsync("http://down.test/v1", "qwen3:8b", null);
        Assert.Equal(SummaryProbeStatus.Unreachable, down.SummaryProbe!.Status);

        var badAddress = await client.ProbeSummaryAsync("ftp://x", "qwen3:8b", null);
        Assert.Equal(ProtocolErrors.InvalidArgument, badAddress.Error);
        await host.DisposeAsync();
    }
}
