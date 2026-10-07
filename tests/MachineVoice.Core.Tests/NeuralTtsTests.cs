using System.Collections.Concurrent;
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

    [Fact]
    public void Split_PacksSentences_UpToTheLimits_AndEndsHeadingsWithAStop()
    {
        var sentence = "Сборка прошла, и все тесты на этот раз тоже прошли.";
        var text = "Итоги\n" + string.Join(" ", Enumerable.Repeat(sentence, 9));

        var chunks = SpeechChunks.Split(text, pack: true);

        Assert.StartsWith("Итоги. " + sentence, chunks[0].Text);
        Assert.True(chunks[0].Text.Length <= SpeechChunks.FirstLimit);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= SpeechChunks.Limit));
        Assert.True(chunks.Count < SpeechChunks.Split(text).Count);
        Assert.Equal(SpeechChunks.Words(text), chunks.SelectMany(chunk => chunk.Words));
        var first = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(first, chunk.FirstWord);
            Assert.Equal(SpeechChunks.Words(chunk.Text), chunk.Words);
            first += chunk.Words.Count;
        }
    }

    [Fact]
    public void WordStarts_MapsMarksToTheClientsWords_AndFillsTheGaps()
    {
        // Words: Готово, файл, Program, cs. The first mark starts at the quote, "cs" has no mark of its own.
        var starts = SpeechChunks.WordStarts("«Готово», файл Program.cs.", [(0, 0.1), (10, 0.8), (15, 1.2)]);

        Assert.Equal(new[] { 0.1, 0.8, 1.2, 1.2 }, starts);
    }
}

public class ChunkedAudioEngineTests
{
    [Fact(Timeout = 20000)]
    public async Task PlaybackRate_ReachesThePlayer_WhileAClipPlays()
    {
        var player = new FakeAudioPlayer();
        using var tts = new ChunkedAudioEngine(player, new FakeSynthesizer(), "fake", TimeSpan.FromSeconds(5));

        tts.Speak("u1", "Одна фраза.");
        await player.WaitForPlayAsync(1);
        tts.Apply(new TtsSettingsDto { PlaybackRate = 1.5 });

        Assert.Equal(1.5, player.Rate);
        Assert.True(player.IsActive);
    }

    [Fact(Timeout = 20000)]
    public async Task WordStarts_OfTheSynthesizer_DriveTheProgress()
    {
        var player = new FakeAudioPlayer();
        var synthesizer = new FakeSynthesizer(_ => new SpeechAudio([1, 2, 3], [0, 0.2, 0.7]));
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5));
        var recorder = new TtsRecorder(tts);

        tts.Speak("u1", "раз два три");
        await player.WaitForPlayAsync(1);

        // A position-based estimate would still be at the first word (0.3 of 3 words).
        player.Position = 0.3;
        await WaitForWordAsync(recorder, 1);
        player.Position = 0.75;
        await WaitForWordAsync(recorder, 2);
        player.End();

        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new[] { "раз", "два", "три" }, recorder.Words.Select(w => w.Word));
    }

    [Fact(Timeout = 20000)]
    public async Task EverySession_EndsOnce_WhenReplacedStoppedOrFinished()
    {
        var player = new FakeAudioPlayer();
        var synthesizer = new FakeSynthesizer();
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5));
        var recorder = new TtsRecorder(tts);

        tts.Speak("replaced", "Первая.");
        tts.Speak("stopped", "Вторая.");
        Assert.Equal(1, synthesizer.Ended);
        await player.WaitForPlayAsync(1);
        tts.Stop();
        Assert.Equal(2, synthesizer.Ended);

        var played = player.PlayedCount;
        tts.Speak("finished", "Третья.");
        await player.WaitForPlayAsync(played + 1);
        player.End();
        Assert.Equal("finished", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, synthesizer.Begun);
        Assert.Equal(3, synthesizer.Ended);
    }

    [Fact(Timeout = 20000)]
    public async Task Seek_IntoAChunkNotSynthesized_SkipsToIt_AndStartsAtTheTime()
    {
        var player = new FakeAudioPlayer();
        var texts = new ConcurrentQueue<string>();
        var synthesizer = new FakeSynthesizer(text =>
        {
            texts.Enqueue(text);
            return new SpeechAudio([1, 2, 3]);
        });
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5), lookahead: 0);
        var recorder = new TtsRecorder(tts);
        var positions = new ConcurrentQueue<TtsPositionEventArgs>();
        tts.PositionChanged += (_, args) => positions.Enqueue(args);

        // The fake clips last 1 s, so the chunks of the same length are estimated at 1 s each.
        tts.Speak("u1", "Первая фраза. Вторая фраза. Третья фраза.");
        await player.WaitForPlayAsync(1);
        tts.Seek(2.5);
        await player.WaitForPlayAsync(2);

        Assert.Equal(new[] { "Первая фраза.", "Третья фраза." }, texts);
        Assert.Equal(0.5, player.Seeks.Last(), 6);
        Assert.Contains(positions, p => Math.Abs(p.Position - 2.5) < 1e-6 && Math.Abs(p.Duration - 3) < 1e-6);
        await WaitForWordAsync(recorder, 5);
        player.End();

        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(recorder.Words, w => w.WordIndex is 2 or 3);
    }

    [Fact(Timeout = 20000)]
    public async Task Seek_WithinTheClip_MovesThePlayer_AndBackReplaysAnEarlierChunk()
    {
        var player = new FakeAudioPlayer();
        var synthesizer = new FakeSynthesizer(text => new SpeechAudio(text.StartsWith("Первая") ? [1] : [2]));
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5));
        var recorder = new TtsRecorder(tts);

        tts.Speak("u1", "Первая фраза. Вторая фраза.");
        await player.WaitForPlayAsync(1);
        tts.Seek(0.4);
        Assert.Equal(new[] { 0.4 }, player.Seeks);
        Assert.Equal(1, player.PlayedCount);

        player.End();
        await player.WaitForPlayAsync(2);
        tts.Seek(0.25);
        await player.WaitForPlayAsync(3);

        Assert.Equal(new byte[][] { [1], [2], [1] }, player.Played);
        Assert.Equal(0.25, player.Seeks.Last(), 6);
        player.End();
        await player.WaitForPlayAsync(4);
        player.End();
        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact(Timeout = 20000)]
    public async Task Seek_DuringAPause_StaysPaused_UntilResume()
    {
        var player = new FakeAudioPlayer();
        using var tts = new ChunkedAudioEngine(player, new FakeSynthesizer(), "fake", TimeSpan.FromSeconds(5));

        tts.Speak("u1", "Первая фраза. Вторая фраза.");
        await player.WaitForPlayAsync(1);
        tts.Pause();
        tts.Seek(1.5);
        await Task.Delay(200);
        Assert.Equal(1, player.PlayedCount);
        Assert.False(player.IsActive);

        tts.Resume();
        await player.WaitForPlayAsync(2);
        Assert.False(player.IsPaused);
        Assert.Equal(0.5, player.Seeks.Last(), 6);
    }

    [Fact(Timeout = 20000)]
    public async Task Lookahead_SynthesizesFiveChunksPastTheOneThatPlays()
    {
        var count = 0;
        var player = new FakeAudioPlayer();
        var synthesizer = new FakeSynthesizer(_ =>
        {
            Interlocked.Increment(ref count);
            return new SpeechAudio([1]);
        });
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5));

        tts.Speak("u", "Раз один. Два два. Три три. Четыре четыре. Пять пять. Шесть шесть. Семь семь. Восемь восемь.");
        await WaitForCountAsync(() => count, 6);

        await Task.Delay(80);
        Assert.Equal(6, Volatile.Read(ref count));
        Assert.Equal(1, player.PlayedCount);
    }

    [Fact(Timeout = 20000)]
    public async Task Prepare_IsPlayedBySpeak_WithoutSynthesizingAgain()
    {
        var count = 0;
        var player = new FakeAudioPlayer();
        var synthesizer = new FakeSynthesizer(_ =>
        {
            Interlocked.Increment(ref count);
            return new SpeechAudio([1, 2, 3]);
        });
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5));

        tts.Prepare("u", "Одна фраза.");
        await WaitForCountAsync(() => count, 1);
        Assert.Equal(0, player.PlayedCount);

        tts.Speak("u", "Одна фраза.");
        await player.WaitForPlayAsync(1);
        await Task.Delay(50);
        Assert.Equal(1, synthesizer.Begun);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact(Timeout = 20000)]
    public async Task Prepare_WaitsWhileThePlayingUtteranceStillNeedsChunks()
    {
        var count = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new FakeAudioPlayer();
        var synthesizer = new YieldingSynthesizer(async (text, cancellationToken) =>
        {
            var n = Interlocked.Increment(ref count);
            if (n == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return new SpeechAudio([1]);
        });
        using var tts = new ChunkedAudioEngine(player, synthesizer, "fake", TimeSpan.FromSeconds(5));

        tts.Speak("playing", "Раз один. Два два. Три три. Четыре четыре. Пять пять. Шесть шесть.");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tts.Prepare("next", "Дальше.");
        await Task.Delay(80);
        Assert.Equal(1, Volatile.Read(ref count));

        release.TrySetResult();
        await WaitForCountAsync(() => count, 7);
    }

    sealed class YieldingSynthesizer(Func<string, CancellationToken, Task<SpeechAudio>> synthesize) : IChunkSynthesizer
    {
        public ISynthesisSession Begin() => new Session(synthesize);

        sealed class Session(Func<string, CancellationToken, Task<SpeechAudio>> synthesize) : ISynthesisSession
        {
            public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
                synthesize(text, cancellationToken);

            public void Dispose()
            {
            }
        }
    }

    static async Task WaitForCountAsync(Func<int> count, int expected)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (count() < expected)
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Synthesized {count()}, expected {expected}.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void WavSeconds_ReadsTheHeader()
    {
        Assert.Equal(0.5, WavFile.Seconds(WavFile.Mono16(new float[11025], 22050)));
        Assert.Null(WavFile.Seconds([1, 2, 3]));
    }

    [Fact]
    public void TrimStart_CutsTheQuietBeforeSpeech_MutesTheClick_AndFadesTheEnds()
    {
        const int rate = 1000;
        // A click, 0.8 s of quiet, a second of speech that stops at full level.
        var samples = new float[40 + 760 + 1000];
        samples.AsSpan(0, 40).Fill(0.05f);
        for (var i = 800; i < samples.Length; i++)
            samples[i] = i % 2 == 0 ? 0.4f : -0.4f;

        var trimmed = WavFile.TrimStart(WavFile.Mono16(samples, rate), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(40));

        Assert.Equal(1.25, WavFile.Seconds(trimmed)!.Value, 2);
        var pcm = Pcm(trimmed);
        Assert.All(pcm[..250], sample => Assert.Equal(0, sample));
        Assert.InRange(Math.Abs((int)pcm[600]), 13000, 13200);
        Assert.True(Math.Abs((int)pcm[^1]) < 0.1 * short.MaxValue);

        // Speech within the lead keeps the clip as long as it is; only the click goes.
        var early = new float[400];
        early.AsSpan(0, 40).Fill(0.05f);
        early.AsSpan(200).Fill(0.4f);
        var kept = Pcm(WavFile.TrimStart(WavFile.Mono16(early, rate), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(40)));
        Assert.Equal(400, kept.Length);
        Assert.All(kept[..40], sample => Assert.Equal(0, sample));

        byte[] notPcm = [1, 2, 3];
        Assert.Same(notPcm, WavFile.TrimStart(notPcm, TimeSpan.FromMilliseconds(250), TimeSpan.Zero));
        var silent = WavFile.Mono16(new float[500], rate);
        Assert.Same(silent, WavFile.TrimStart(silent, TimeSpan.FromMilliseconds(250), TimeSpan.Zero));
    }

    static short[] Pcm(byte[] wav)
    {
        var samples = new short[(wav.Length - 44) / 2];
        Buffer.BlockCopy(wav, 44, samples, 0, samples.Length * 2);
        return samples;
    }

    static async Task WaitForWordAsync(TtsRecorder recorder, int wordIndex)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!recorder.Words.Any(w => w.WordIndex == wordIndex))
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"Word {wordIndex} was not reported.");
            await Task.Delay(10);
        }
    }

    sealed class FakeSynthesizer(Func<string, SpeechAudio>? synthesize = null) : IChunkSynthesizer
    {
        int _begun;
        int _ended;

        Func<string, SpeechAudio>? Synthesize { get; } = synthesize;

        public int Begun => Volatile.Read(ref _begun);
        public int Ended => Volatile.Read(ref _ended);

        public ISynthesisSession Begin()
        {
            Interlocked.Increment(ref _begun);
            return new Session(this);
        }

        sealed class Session(FakeSynthesizer owner) : ISynthesisSession
        {
            public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Synthesize?.Invoke(text) ?? new SpeechAudio([1, 2, 3]));

            public void Dispose() => Interlocked.Increment(ref owner._ended);
        }
    }
}

public class QwenEngineTests
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
        using var tts = QwenSynthesizer.Engine(player, server, http);
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
        using var tts = QwenSynthesizer.Engine(player, httpHandler: http);
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
        using var tts = QwenSynthesizer.Engine(player, httpHandler: http);
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
        using var tts = QwenSynthesizer.Engine(player, httpHandler: http);
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
        using var tts = QwenSynthesizer.Engine(new FakeAudioPlayer(), server, http);

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
        using var tts = new TtsSwitch(system, Neural(() =>
        {
            created++;
            return qwen;
        }));

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
        using var tts = new TtsSwitch(system, Neural(() => qwen));
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
    public void SystemVoiceFailure_HandsTheRestToTheLastResort_AndSettingsReachEveryEngine()
    {
        var system = new ManualNeuralEngine();
        var qwen = new ManualNeuralEngine();
        var lastResort = new ManualTtsEngine();
        using var tts = new TtsSwitch(system, Neural(() => qwen), lastResort: lastResort);
        var recorder = new TtsRecorder(tts);
        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen, PlaybackRate = 1.5 });
        Assert.Equal(1.5, system.Applied!.PlaybackRate);

        tts.Speak("u", "Раз два. Три четыре. Пять.");
        qwen.Fail("Три четыре. Пять.", wordOffset: 2);
        Assert.Equal("u", system.UtteranceId);
        system.Fail("Пять.", wordOffset: 2);
        Assert.Equal("Пять.", lastResort.Text);

        lastResort.Emit(0, "Пять");
        lastResort.Complete();
        Assert.Equal(new[] { (4, "Пять") }, recorder.Words.Select(w => (w.WordIndex, w.Word)));
        Assert.True(recorder.Completed.Task.IsCompleted);
    }

    [Fact]
    public void SystemVoiceFailure_WithoutALastResort_Completes()
    {
        var system = new ManualNeuralEngine();
        using var tts = new TtsSwitch(system, Neural(() => new ManualNeuralEngine()));
        var recorder = new TtsRecorder(tts);
        tts.Apply(new TtsSettingsDto());

        tts.Speak("u", "Раз. Два.");
        system.Fail("Два.", wordOffset: 1);

        Assert.True(recorder.Completed.Task.IsCompleted);
    }

    [Fact]
    public void Seek_AfterAFallback_CountsTheTimeTheFailedEngineRead()
    {
        var system = new ManualTtsEngine();
        var qwen = new ManualNeuralEngine();
        using var tts = new TtsSwitch(system, Neural(() => qwen));
        var positions = new List<TtsPositionEventArgs>();
        tts.PositionChanged += (_, args) => positions.Add(args);
        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });

        tts.Speak("u", "Раз два. Три четыре.");
        qwen.EmitPosition(1.5, 4);
        tts.Seek(1);
        Assert.Equal(new[] { 1.0 }, qwen.Seeks);

        qwen.Fail("Три четыре.", wordOffset: 2);
        system.EmitPosition(0.5, 2);
        tts.Seek(3);
        tts.Seek(0.2);

        Assert.Equal(new[] { 1.5, 0.0 }, system.Seeks);
        Assert.Equal(new[] { (1.5, 4.0), (2.0, 3.5) }, positions.Select(p => (p.Position, p.Duration)));
    }

    [Fact]
    public void Stop_IgnoresLateEventsOfTheStoppedUtterance()
    {
        var system = new ManualTtsEngine();
        var qwen = new ManualNeuralEngine();
        using var tts = new TtsSwitch(system, Neural(() => qwen));
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

    [Fact]
    public void EachNeuralEngine_IsCreatedWhenSelected_AndFallsBackToTheSystemVoice()
    {
        var system = new ManualTtsEngine();
        var qwen = new ManualNeuralEngine();
        var omni = new ManualNeuralEngine();
        using var tts = new TtsSwitch(system, new Dictionary<TtsEngineKind, Func<ITtsEngine>>
        {
            [TtsEngineKind.Qwen] = () => qwen,
            [TtsEngineKind.OmniVoice] = () => omni,
        });
        var recorder = new TtsRecorder(tts);

        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.OmniVoice });
        Assert.Null(qwen.Applied);
        tts.Speak("a", "Раз. Два.");
        Assert.Equal("a", omni.UtteranceId);
        omni.Fail("Два.", wordOffset: 1);
        Assert.Equal("Два.", system.Text);
        system.Complete();
        Assert.True(recorder.Completed.Task.IsCompleted);

        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });
        Assert.Equal(TtsEngineKind.Qwen, omni.Applied!.Engine);
        tts.Speak("b", "три");
        Assert.Equal("b", qwen.UtteranceId);
        Assert.Null(omni.UtteranceId);
    }

    static Dictionary<TtsEngineKind, Func<ITtsEngine>> Neural(Func<ITtsEngine> qwen) => new() { [TtsEngineKind.Qwen] = qwen };

    sealed class ManualNeuralEngine : IFallibleTtsEngine, ISeekableTtsEngine, IConfigurableTts
    {
        readonly ManualTtsEngine _inner = new();

        public ManualNeuralEngine()
        {
            _inner.Progress += (_, args) => Progress?.Invoke(this, args);
            _inner.Completed += (_, args) => Completed?.Invoke(this, args);
            _inner.PositionChanged += (_, args) => PositionChanged?.Invoke(this, args);
        }

        public event EventHandler<TtsProgressEventArgs>? Progress;
        public event EventHandler<TtsCompletedEventArgs>? Completed;
        public event EventHandler<TtsFailedEventArgs>? Failed;
        public event EventHandler<TtsPositionEventArgs>? PositionChanged;

        public List<double> Seeks => _inner.Seeks;
        public void Seek(double position) => _inner.Seek(position);
        public void EmitPosition(double position, double duration) => _inner.EmitPosition(position, duration);

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
    public async Task PlaybackRate_IsValidated_AppliedAtOnce_AndSurvivesRestart()
    {
        await using var root = new TempRoot();
        var tts = new ConfigurableTts();
        var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        await log.TakeAsync<SnapshotEvent>();

        foreach (var bad in new[] { 0.4, 2.1, double.NaN })
            Assert.Equal(ProtocolErrors.InvalidArgument, (await client.SetPlaybackRateAsync(bad)).Error);
        var badVoice = await client.UpdateSettingsAsync(tts: new TtsSettingsDto { SystemVoice = new SystemVoiceSettingsDto { Rate = 1.5 } });
        Assert.Equal(ProtocolErrors.InvalidArgument, badVoice.Error);

        Assert.True((await client.SetPlaybackRateAsync(1.25)).Ok);
        var changed = await log.TakeAsync<SettingsChangedEvent>();
        Assert.Equal(1.25, changed.Settings.Tts.PlaybackRate);
        Assert.Equal(1.25, tts.Applied.Last().PlaybackRate);
        Assert.Equal(TtsEngineKind.System, tts.Applied.Last().Engine);

        var updated = await client.UpdateSettingsAsync(tts: new TtsSettingsDto
        {
            PlaybackRate = 1.25,
            SystemVoice = new SystemVoiceSettingsDto { Rate = 0.6 },
        });
        Assert.True(updated.Ok);
        await host.DisposeAsync();

        var restartedTts = new ConfigurableTts();
        var restarted = await TestHost.StartAsync(root.Path, restartedTts);
        var applied = Assert.Single(restartedTts.Applied);
        Assert.Equal(1.25, applied.PlaybackRate);
        Assert.Equal(0.6, applied.SystemVoice.Rate);
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
        Assert.Equal(TtsSettingsDto.DefaultPlaybackRate, settings.Tts.PlaybackRate);
        Assert.Equal(SystemVoiceSettingsDto.DefaultRate, settings.Tts.SystemVoice.Rate);
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
