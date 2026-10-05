using System.Net;
using System.Text;
using System.Text.Json;
using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class SpeechChunksTests
{
    [Fact]
    public void Split_KeepsSentences_AndCountsWordsLikeTheClient()
    {
        var chunks = SpeechChunks.Split("Готово, сборка прошла. Тесты зелёные!\nОсталось 2 вопроса… Ответишь?");

        Assert.Equal(new[] { "Готово, сборка прошла.", "Тесты зелёные!", "Осталось 2 вопроса…", "Ответишь?" }, chunks.Select(c => c.Text));
        Assert.Equal(new[] { 0, 3, 5, 8 }, chunks.Select(c => c.FirstWord));
        Assert.Equal(new[] { "Осталось", "2", "вопроса" }, chunks[2].Words);
    }

    [Fact]
    public void Split_DoesNotBreakNumbersOrNames()
    {
        var chunks = SpeechChunks.Split("Версия 1.5 вышла. Файл Program.cs поправлен.");

        Assert.Equal(new[] { "Версия 1.5 вышла.", "Файл Program.cs поправлен." }, chunks.Select(c => c.Text));
    }

    [Fact]
    public void Split_JoinsLoneWords_WithTheNextSentence()
    {
        var chunks = SpeechChunks.Split("1. Сделал сборку.\nДа. Всё.");

        Assert.Equal(new[] { "1. Сделал сборку.", "Да. Всё." }, chunks.Select(c => c.Text));
    }

    [Fact]
    public void Split_BoundsLongSentences_AtClauses()
    {
        var clause = string.Join(" ", Enumerable.Repeat("слово", 12));
        var sentence = string.Join(", ", Enumerable.Repeat(clause, 8)) + ".";

        var chunks = SpeechChunks.Split(sentence);

        Assert.True(chunks.Count > 2);
        Assert.True(chunks[0].Text.Length <= SpeechChunks.FirstLimit);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= SpeechChunks.Limit));
        Assert.All(chunks.SkipLast(1), chunk => Assert.EndsWith(",", chunk.Text));
        Assert.Equal(96, chunks.Sum(chunk => chunk.Words.Count));
    }

    [Fact]
    public void Split_SkipsTextWithoutWords()
    {
        Assert.Empty(SpeechChunks.Split(" … — "));
    }
}

public class NeuralTtsEngineTests
{
    static readonly TtsSettingsDto Qwen = new()
    {
        Engine = TtsEngineKind.Qwen,
        Qwen = new QwenTtsSettingsDto { Endpoint = "http://tts.test/v1/", Voice = "Serena", UnloadAfterMinutes = 5 },
    };

    [Fact(Timeout = 20000)]
    public async Task Speak_SynthesizesSentencesInOrder_ThenCompletes()
    {
        var player = new FakeAudioPlayer();
        var server = new FakeSpeechServer();
        var http = new StubHttpHandler((_, body, _) => Task.FromResult(Wav(body)));
        using var tts = new NeuralTtsEngine(player, server, http);
        var recorder = new TtsRecorder(tts);
        tts.Apply(Qwen);

        tts.Speak("u1", "Первая фраза. Вторая фраза! Третья?");
        for (var i = 1; i <= 3; i++)
        {
            await player.WaitForPlayAsync(i);
            await Task.Delay(60);
            player.End();
        }

        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var speech = http.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/audio/speech")).ToList();
        Assert.Equal(new[] { "Первая фраза.", "Вторая фраза!", "Третья?" }, speech.Select(r => Field(r.Body, "input")));
        Assert.All(speech, r => Assert.Equal("http://tts.test/v1/audio/speech", r.Request.RequestUri!.ToString()));
        Assert.All(speech, r => Assert.Equal("Serena", Field(r.Body, "voice")));
        Assert.All(speech, r => Assert.Equal("wav", Field(r.Body, "response_format")));
        Assert.All(speech, r => Assert.Equal(1.05, Number(r.Body, "repetition_penalty")));
        Assert.Equal(new[] { 0, 2, 4 }, recorder.Words.Select(w => w.WordIndex).Distinct());
        Assert.Contains("ensure http://tts.test/v1/", server.Calls);
        Assert.Equal("idle 5", server.Calls.Last());
    }

    [Fact(Timeout = 20000)]
    public async Task Pause_BeforeTheAudioIsReady_HoldsPlayback_UntilResume()
    {
        var player = new FakeAudioPlayer();
        var answer = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var http = new StubHttpHandler((request, body, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/audio/speech") ? answer.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var tts = new NeuralTtsEngine(player, httpHandler: http);
        tts.Apply(Qwen);

        tts.Speak("u1", "Одна фраза.");
        tts.Pause();
        answer.SetResult(Wav("{}"));
        await Task.Delay(300);
        Assert.Equal(0, player.PlayedCount);

        tts.Resume();
        await player.WaitForPlayAsync(1);
        tts.Pause();
        Assert.True(player.IsPaused);
        tts.Resume();
        Assert.False(player.IsPaused);
    }

    [Fact(Timeout = 20000)]
    public async Task Stop_DoesNotComplete_AndSilencesThePlayer()
    {
        var player = new FakeAudioPlayer();
        var http = new StubHttpHandler((_, body, _) => Task.FromResult(Wav(body)));
        using var tts = new NeuralTtsEngine(player, httpHandler: http);
        var recorder = new TtsRecorder(tts);
        tts.Apply(Qwen);

        tts.Speak("u1", "Первая. Вторая.");
        await player.WaitForPlayAsync(1);
        tts.Stop();
        Assert.False(player.IsActive);

        await Task.Delay(300);
        Assert.Equal(1, player.PlayedCount);
        Assert.False(recorder.Completed.Task.IsCompleted);
    }

    [Fact(Timeout = 20000)]
    public async Task FailedChunk_ReportsTheUnreadText()
    {
        var player = new FakeAudioPlayer();
        var calls = 0;
        var http = new StubHttpHandler((request, body, _) =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/audio/speech"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? Wav(body)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("model crashed") });
        });
        using var tts = new NeuralTtsEngine(player, httpHandler: http);
        var recorder = new TtsRecorder(tts);
        tts.Apply(Qwen);

        tts.Speak("u1", "Первая фраза. Вторая фраза! Третья?");
        await player.WaitForPlayAsync(1);
        player.End();

        var failed = await recorder.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("u1", failed.UtteranceId);
        Assert.Equal("Вторая фраза! Третья?", failed.RemainingText);
        Assert.Equal(2, failed.WordOffset);
        Assert.Contains("model crashed", failed.Error.Message);
        Assert.False(recorder.Completed.Task.IsCompleted);
    }

    [Fact(Timeout = 20000)]
    public async Task SelectingTheEngine_LoadsTheModel()
    {
        var server = new FakeSpeechServer();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var tts = new NeuralTtsEngine(new FakeAudioPlayer(), server, http);

        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.System, Qwen = Qwen.Qwen });
        tts.Apply(Qwen);

        var deadline = Environment.TickCount64 + 5000;
        while (!server.Calls.Contains("idle 5") && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        var (request, _) = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "http://tts.test/v1/models?model_name=" + Uri.EscapeDataString(QwenTtsSettingsDto.DefaultModel),
            request.RequestUri!.ToString());
        Assert.Equal(new[] { "busy", "ensure http://tts.test/v1/", "idle 5" }, server.Calls);
    }

    static HttpResponseMessage Wav(string body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('w', 64) + body)),
    };

    static string? Field(string body, string name)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty(name).GetString();
    }

    static double Number(string body, string name)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty(name).GetDouble();
    }
}

public class TtsSwitchTests
{
    [Fact]
    public void Speak_GoesToTheSelectedEngine_FromTheNextUtterance()
    {
        var system = new ManualTtsEngine();
        var qwen = new ManualNeuralEngine();
        var created = 0;
        using var tts = new TtsSwitch(system, () =>
        {
            created++;
            return qwen;
        });

        tts.Apply(new TtsSettingsDto());
        Assert.Equal(0, created);
        tts.Speak("a", "раз");
        Assert.Equal("a", system.UtteranceId);

        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });
        Assert.Equal(TtsEngineKind.Qwen, qwen.Applied!.Engine);
        tts.Pause();
        Assert.True(system.IsPaused);
        Assert.False(qwen.IsPaused);

        tts.Speak("b", "два");
        Assert.Null(system.UtteranceId);
        Assert.Equal("b", qwen.UtteranceId);
        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Failure_HandsTheRestToTheSystemVoice_WithShiftedWordIndices()
    {
        var system = new ManualTtsEngine();
        var qwen = new ManualNeuralEngine();
        using var tts = new TtsSwitch(system, () => qwen);
        var recorder = new TtsRecorder(tts);
        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });

        tts.Speak("u", "Раз два. Три четыре.");
        qwen.Emit(0, "Раз");
        qwen.Fail("Три четыре.", wordOffset: 2);
        Assert.Equal("u", system.UtteranceId);
        Assert.Equal("Три четыре.", system.Text);

        system.Emit(1, "четыре");
        tts.Pause();
        Assert.True(system.IsPaused);
        system.Complete();

        Assert.Equal(new[] { (0, "Раз"), (3, "четыре") }, recorder.Words.Select(w => (w.WordIndex, w.Word)));
        Assert.True(recorder.Completed.Task.IsCompleted);
        Assert.Equal("u", await recorder.Completed.Task);
    }

    [Fact]
    public void Stop_IgnoresLateEventsOfTheStoppedUtterance()
    {
        var system = new ManualTtsEngine();
        var qwen = new ManualNeuralEngine();
        using var tts = new TtsSwitch(system, () => qwen);
        var recorder = new TtsRecorder(tts);
        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });

        tts.Speak("u", "текст");
        var late = qwen.UtteranceId!;
        tts.Stop();
        qwen.Speak(late, "текст");
        qwen.Fail("текст", 0);

        Assert.Null(system.UtteranceId);
        Assert.Empty(recorder.Words);
        Assert.False(recorder.Completed.Task.IsCompleted);
    }

    sealed class ManualNeuralEngine : IFallibleTtsEngine, IConfigurableTts
    {
        readonly ManualTtsEngine _inner = new();

        public ManualNeuralEngine()
        {
            _inner.Progress += (_, args) => Progress?.Invoke(this, args);
            _inner.Completed += (_, args) => Completed?.Invoke(this, args);
        }

        public event EventHandler<TtsProgressEventArgs>? Progress;
        public event EventHandler<TtsCompletedEventArgs>? Completed;
        public event EventHandler<TtsFailedEventArgs>? Failed;

        public TtsSettingsDto? Applied { get; private set; }
        public string? UtteranceId => _inner.UtteranceId;
        public bool IsPaused => _inner.IsPaused;

        public void Apply(TtsSettingsDto settings) => Applied = settings;
        public void Speak(string utteranceId, string text) => _inner.Speak(utteranceId, text);
        public void Pause() => _inner.Pause();
        public void Resume() => _inner.Resume();
        public void Stop() => _inner.Stop();
        public void Emit(int wordIndex, string word) => _inner.Emit(wordIndex, word);

        public void Fail(string remaining, int wordOffset)
        {
            var id = _inner.UtteranceId!;
            _inner.Stop();
            Failed?.Invoke(this, new TtsFailedEventArgs(id, remaining, wordOffset, new HttpRequestException("down")));
        }
    }
}

public class TtsSettingsTests
{
    [Fact(Timeout = 20000)]
    public async Task TtsSettings_AreValidated_Applied_AndSurviveRestart()
    {
        await using var root = new TempRoot();
        var tts = new ConfigurableTts();
        var host = await TestHost.StartAsync(root.Path, tts);
        Assert.Equal(TtsEngineKind.System, tts.Applied.Single().Engine);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        await log.TakeAsync<SnapshotEvent>();

        var bad = new[]
        {
            new QwenTtsSettingsDto { Endpoint = "ftp://x" },
            new QwenTtsSettingsDto { Model = " " },
            new QwenTtsSettingsDto { Voice = "" },
            new QwenTtsSettingsDto { UnloadAfterMinutes = -1 },
        };
        foreach (var qwen in bad)
        {
            var rejected = await client.UpdateSettingsAsync(tts: new TtsSettingsDto { Engine = TtsEngineKind.Qwen, Qwen = qwen });
            Assert.Equal(ProtocolErrors.InvalidArgument, rejected.Error);
        }

        Assert.Single(tts.Applied);
        var updated = await client.UpdateSettingsAsync(tts: new TtsSettingsDto
        {
            Engine = TtsEngineKind.Qwen,
            Qwen = new QwenTtsSettingsDto { Endpoint = " http://127.0.0.1:9000/v1 ", Voice = " Vivian ", UnloadAfterMinutes = 0 },
        });
        Assert.True(updated.Ok);
        var changed = await log.TakeAsync<SettingsChangedEvent>();
        Assert.Equal(TtsEngineKind.Qwen, changed.Settings.Tts.Engine);
        Assert.Equal("http://127.0.0.1:9000/v1", changed.Settings.Tts.Qwen.Endpoint);
        Assert.Equal("Vivian", tts.Applied.Last().Qwen.Voice);
        await host.DisposeAsync();

        var restartedTts = new ConfigurableTts();
        var restarted = await TestHost.StartAsync(root.Path, restartedTts);
        var applied = Assert.Single(restartedTts.Applied);
        Assert.Equal(TtsEngineKind.Qwen, applied.Engine);
        Assert.Equal(0, applied.Qwen.UnloadAfterMinutes);
        Assert.Equal(QwenTtsSettingsDto.DefaultModel, applied.Qwen.Model);
        await restarted.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task SettingsWithoutTts_LoadTheDefaults()
    {
        await using var root = new TempRoot();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "settings.json"), """{"mode":"silent","sources":[]}""");
        var tts = new ConfigurableTts();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();

        var settings = (await client.GetSettingsAsync()).Settings!;
        Assert.Equal(PlaybackMode.Silent, settings.Mode);
        Assert.Equal(TtsEngineKind.System, settings.Tts.Engine);
        Assert.Equal(QwenTtsSettingsDto.DefaultEndpoint, settings.Tts.Qwen.Endpoint);
        Assert.Equal(TtsEngineKind.System, Assert.Single(tts.Applied).Engine);
        await host.DisposeAsync();
    }

    sealed class ConfigurableTts : ITtsEngine, IConfigurableTts
    {
        public List<TtsSettingsDto> Applied { get; } = [];

        public event EventHandler<TtsProgressEventArgs>? Progress { add { } remove { } }
        public event EventHandler<TtsCompletedEventArgs>? Completed { add { } remove { } }

        public void Apply(TtsSettingsDto settings) => Applied.Add(settings);
        public void Speak(string utteranceId, string text) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
    }
}
