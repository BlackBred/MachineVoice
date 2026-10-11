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
        LengthLimit = 0,
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
            LengthLimit = 0,
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
            LengthLimit = 0,
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
        var shortLimit = await client.UpdateSettingsAsync(summary: new SummarySettingsDto { LengthLimit = -1 });
        Assert.Equal(ProtocolErrors.InvalidArgument, shortLimit.Error);
        var longLimit = await client.UpdateSettingsAsync(summary: new SummarySettingsDto { LengthLimit = SummarySettingsDto.MaxLengthLimit + 1 });
        Assert.Equal(ProtocolErrors.InvalidArgument, longLimit.Error);
        var longPrompt = await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            SystemPrompt = new string('а', SummarySettingsDto.MaxSystemPrompt + 1),
        });
        Assert.Equal(ProtocolErrors.InvalidArgument, longPrompt.Error);
        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.UpdateSettingsAsync()).Error);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto { Model = "m", SystemPrompt = "   " })).Ok);
        var defaults = (await client.GetSettingsAsync()).Settings!.Summary;
        Assert.Equal(SummarySettingsDto.DefaultSystemPrompt, defaults.SystemPrompt);
        Assert.Equal(SummarySettingsDto.DefaultLengthLimit, defaults.LengthLimit);
        Assert.Equal(ReadingPreference.Summary, defaults.DefaultReading);

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
        Assert.Equal(0, settings.Summary.LengthLimit);
        Assert.Equal(ReadingPreference.Summary, settings.Summary.DefaultReading);
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

    [Fact(Timeout = 20000)]
    public async Task ShortText_IsReadAsIs_AndALongerOneIsRetold()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new StubHttpHandler((_, _, _) => answer.Task);
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Endpoint = Enabled.Endpoint,
            Model = Enabled.Model,
            TimeoutSeconds = Enabled.TimeoutSeconds,
            LengthLimit = 10,
            DefaultReading = ReadingPreference.Summary,
        })).Ok);

        var shortText = new string('а', 10);
        var brief = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "short", shortText));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == brief.Id);
        Assert.Equal("Cursor.\n" + shortText + ".", tts.Text);
        Assert.Empty(http.Requests);
        tts.Complete();
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Idle);

        var longText = new string('б', 11);
        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "long", longText));
        Assert.Null(tts.UtteranceId);
        answer.SetResult(StubHttpHandler.Chat("Суть готова."));
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nСуть готова.", tts.Text);
        Assert.Single(http.Requests);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task DefaultReading_PicksTheRetelling_AndTheToastCanSwitch()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(StubHttpHandler.Chat("Суть готова.")));
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Endpoint = Enabled.Endpoint,
            Model = Enabled.Model,
            TimeoutSeconds = Enabled.TimeoutSeconds,
            LengthLimit = 0,
            DefaultReading = ReadingPreference.Summary,
        })).Ok);

        const string Original = "**длинный** ответ, который надо пересказать целиком";
        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", Original));
        var shown = await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == accepted.Id);
        Assert.True(shown.Item.HasSummary);
        Assert.True(shown.Item.ReadSummary);
        Assert.Equal("Суть готова.", shown.Item.Speech);
        Assert.Null(tts.UtteranceId);

        Assert.Equal(ProtocolErrors.NotFound, (await client.SelectReadingAsync("missing", false)).Error);
        Assert.True((await client.SelectReadingAsync(accepted.Id!, false)).Ok);
        var full = (await client.GetSnapshotAsync()).Snapshot!.Confirmation!;
        Assert.True(full.HasSummary);
        Assert.False(full.ReadSummary);
        Assert.Equal("длинный ответ, который надо пересказать целиком.", full.Speech);

        Assert.True((await client.SelectReadingAsync(accepted.Id!, true)).Ok);
        Assert.True((await client.GetSnapshotAsync()).Snapshot!.Confirmation!.ReadSummary);
        Assert.True((await client.ListenAsync(accepted.Id!)).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nСуть готова.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task MissingRetelling_FallsBackToTheFullText()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Endpoint = Enabled.Endpoint,
            Model = Enabled.Model,
            TimeoutSeconds = Enabled.TimeoutSeconds,
            LengthLimit = 0,
            DefaultReading = ReadingPreference.Summary,
        })).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "короткий ответ"));
        var shown = await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == accepted.Id);
        Assert.False(shown.Item.HasSummary);
        Assert.False(shown.Item.ReadSummary);
        Assert.Equal("короткий ответ.", shown.Item.Speech);
        Assert.Equal(ProtocolErrors.InvalidState, (await client.SelectReadingAsync(accepted.Id!, true)).Error);

        Assert.True((await client.ListenAsync(accepted.Id!)).Ok);
        await log.TakeAsync<PlayerStateEvent>(state => state.State == PlayerState.Speaking && state.ItemId == accepted.Id);
        Assert.Equal("Cursor.\nкороткий ответ.", tts.Text);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task FullTextPreference_SpeaksTheOriginal_UntilSwitched()
    {
        await using var root = new TempRoot();
        var tts = new ManualTtsEngine();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(StubHttpHandler.Chat("Суть готова.")));
        var host = await TestHost.StartAsync(root.Path, tts, http);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        Assert.True((await client.SetModeAsync(PlaybackMode.Confirm)).Ok);
        Assert.True((await client.UpdateSettingsAsync(summary: new SummarySettingsDto
        {
            Enabled = true,
            Endpoint = Enabled.Endpoint,
            Model = Enabled.Model,
            TimeoutSeconds = Enabled.TimeoutSeconds,
            LengthLimit = 0,
            DefaultReading = ReadingPreference.Full,
        })).Ok);

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "g", "полный текст ответа"));
        var shown = await log.TakeAsync<ConfirmationRequestedEvent>(confirmation => confirmation.Item.Id == accepted.Id);
        Assert.True(shown.Item.HasSummary);
        Assert.False(shown.Item.ReadSummary);
        Assert.Equal("полный текст ответа.", shown.Item.Speech);

        Assert.True((await client.SelectReadingAsync(accepted.Id!, true)).Ok);
        var summary = (await client.GetSnapshotAsync()).Snapshot!.Confirmation!;
        Assert.True(summary.ReadSummary);
        Assert.Equal("Суть готова.", summary.Speech);
        await host.DisposeAsync();
    }
}
