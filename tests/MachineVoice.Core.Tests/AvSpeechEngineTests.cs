using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using MachineVoice.Core;
using MachineVoice.Platform.MacOS;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

/// <summary>
/// Drives the real synthesizer at zero volume: callbacks still arrive in real time. The delegate runs on
/// the main queue and the test host never runs the main loop, so every scenario runs in a child process
/// of this assembly (see <see cref="Program"/>), where the main thread does.
/// </summary>
[SupportedOSPlatform("macos")]
public class AvSpeechEngineTests
{
    public const string ChildVerb = "av-speech";

    static readonly Dictionary<string, Func<Task>> Scenarios = new(StringComparer.Ordinal)
    {
        [nameof(Speak_ReportsWordsInOrder_ThenCompletes)] = SpeakReportsWordsInOrder,
        [nameof(Stop_DoesNotComplete_AndNextUtteranceStillSpeaks)] = StopDoesNotComplete,
        [nameof(Speak_ReplacesCurrentUtterance_WithoutCompletingIt)] = SpeakReplacesCurrent,
        [nameof(Pause_HoldsAtWordBoundary_UntilResume)] = PauseHoldsUntilResume,
        [nameof(Host_SpeaksQueuedItem_ThroughSynthesizer)] = HostSpeaksQueuedItem,
        [nameof(Writer_ReturnsAudio_WithAStartForEveryWord)] = WriterReturnsAudio,
        [nameof(Writer_VoiceRate_ChangesTheLength)] = WriterVoiceRate,
        [nameof(WriterEngine_ReportsWordsInOrder_ThenCompletes)] = WriterEngineSpeaks,
    };

    [MacOSFact(Timeout = 60000)]
    public Task Speak_ReportsWordsInOrder_ThenCompletes() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task Stop_DoesNotComplete_AndNextUtteranceStillSpeaks() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task Speak_ReplacesCurrentUtterance_WithoutCompletingIt() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task Pause_HoldsAtWordBoundary_UntilResume() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task Host_SpeaksQueuedItem_ThroughSynthesizer() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task Writer_ReturnsAudio_WithAStartForEveryWord() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task Writer_VoiceRate_ChangesTheLength() => RunInChildAsync();

    [MacOSFact(Timeout = 60000)]
    public Task WriterEngine_ReportsWordsInOrder_ThenCompletes() => RunInChildAsync();

    /// <summary>Child process entry: runs the scenario while the main thread runs the main loop.</summary>
    public static int RunChild(string scenario)
    {
        if (!Scenarios.TryGetValue(scenario, out var run))
        {
            Console.Error.WriteLine($"Unknown scenario {scenario}.");
            return 2;
        }

        using var done = new CancellationTokenSource();
        var exitCode = 1;
        _ = Task.Run(async () =>
        {
            try
            {
                await run();
                exitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
            }
            finally
            {
                done.Cancel();
            }
        });
        MacMainLoop.Run(done.Token);
        return exitCode;
    }

    static async Task RunInChildAsync([CallerMemberName] string scenario = "")
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(AvSpeechEngineTests).Assembly.Location);
        start.ArgumentList.Add(ChildVerb);
        start.ArgumentList.Add(scenario);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{scenario} hung.\n{await stdout}\n{await stderr}");
        }

        Assert.True(process.ExitCode == 0, $"{scenario} failed with {process.ExitCode}.\n{await stdout}\n{await stderr}");
    }

    static async Task SpeakReportsWordsInOrder()
    {
        using var tts = Silent();
        var recorder = new Recorder(tts);

        tts.Speak("u1", "один два три");

        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        var words = recorder.Words.ToArray();
        Assert.Equal(new[] { "один", "два", "три" }, words.Select(w => w.Word));
        Assert.Equal(new[] { 0, 1, 2 }, words.Select(w => w.WordIndex));
        Assert.All(words, w => Assert.Equal("u1", w.UtteranceId));
    }

    static async Task StopDoesNotComplete()
    {
        using var tts = Silent();
        var recorder = new Recorder(tts);

        tts.Speak("stopped", "это длинная фраза, которую остановят раньше, чем она закончится");
        await recorder.WaitForWordAsync("stopped");
        tts.Stop();

        tts.Speak("next", "дальше");
        Assert.Equal("next", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.DoesNotContain("stopped", recorder.CompletedIds);
    }

    static async Task SpeakReplacesCurrent()
    {
        using var tts = Silent();
        var recorder = new Recorder(tts);

        tts.Speak("first", "первая фраза, которую перебьёт вторая и до конца не дочитает");
        await recorder.WaitForWordAsync("first");
        tts.Speak("second", "вторая");

        Assert.Equal("second", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(new[] { "second" }, recorder.CompletedIds);
    }

    static async Task PauseHoldsUntilResume()
    {
        using var tts = Silent();
        var recorder = new Recorder(tts);

        tts.Speak("u1", "раз два три четыре пять шесть семь восемь");
        await recorder.WaitForWordAsync("u1");
        tts.Pause();

        // The pause lands at the end of the current word, so one more word may still be reported.
        await Task.Delay(500);
        var pausedAt = recorder.Words.Count;
        await Task.Delay(1000);
        Assert.Equal(pausedAt, recorder.Words.Count);
        Assert.False(recorder.Completed.Task.IsCompleted);

        tts.Resume();
        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(8, recorder.Words.Count);
    }

    static async Task HostSpeaksQueuedItem()
    {
        await using var root = new TempRoot();
        using var tts = Silent();
        await using var host = await TestHost.StartAsync(root.Path, tts);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        await log.TakeAsync<SnapshotEvent>();

        var accepted = await IngestClient.SubmitAsync(host.IngestSocketPath, new SpeechDraft("cursor", "gen-av", "готово"));

        await log.TakeAsync<PlayerStateEvent>(e => e.State == PlayerState.Speaking && e.ItemId == accepted.Id);
        var progress = await log.TakeAsync<PlayerProgressEvent>(e => e.ItemId == accepted.Id, timeoutMs: 15000);
        Assert.False(string.IsNullOrWhiteSpace(progress.Word));
        var spoken = await log.TakeAsync<HistoryAppendedEvent>(timeoutMs: 15000);
        Assert.Equal(SpeechOutcome.Spoken, spoken.Entry.Outcome);
        Assert.Equal(accepted.Id, spoken.Entry.Item.Id);
    }

    static async Task WriterReturnsAudio()
    {
        const string text = "«Готово», сборка прошла. Тесты зелёные!";
        using var writer = new AvSpeechWriter(new AvSpeechOptions { Language = "ru-RU" });
        using var session = writer.Begin();

        var audio = await session.SynthesizeAsync(text, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        var seconds = WavSeconds(audio.Wav);
        Assert.InRange(seconds, 1, 10);
        var starts = audio.WordStarts!;
        Assert.Equal(SpeechChunks.Words(text).Count, starts.Count);
        Assert.Equal(0, starts[0], precision: 2);
        for (var i = 1; i < starts.Count; i++)
            Assert.True(starts[i] > starts[i - 1], $"Word {i} starts at {starts[i]}, not after {starts[i - 1]}.");
        Assert.True(starts[^1] < seconds);

        // The next write must not get buffers of this one.
        var next = await session.SynthesizeAsync("дальше", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(WavSeconds(next.Wav) < seconds);
    }

    static async Task WriterVoiceRate()
    {
        using var writer = new AvSpeechWriter(new AvSpeechOptions { Language = "ru-RU" });
        const string text = "Это фраза, которую голос прочитает сначала медленно, потом быстро.";

        writer.Apply(new TtsSettingsDto { SystemVoice = new SystemVoiceSettingsDto { Rate = 0.35 } });
        using var slow = writer.Begin();
        var slowSeconds = WavSeconds((await slow.SynthesizeAsync(text, CancellationToken.None)).Wav);
        writer.Apply(new TtsSettingsDto { SystemVoice = new SystemVoiceSettingsDto { Rate = 0.65 } });
        using var fast = writer.Begin();
        var fastSeconds = WavSeconds((await fast.SynthesizeAsync(text, CancellationToken.None)).Wav);

        Assert.True(fastSeconds < slowSeconds * 0.8, $"{fastSeconds:0.00} s at 0.65, {slowSeconds:0.00} s at 0.35.");
    }

    static async Task WriterEngineSpeaks()
    {
        using var tts = AvSpeechWriter.Engine(new AvAudioPlayer(volume: 0), new AvSpeechOptions { Language = "ru-RU" });
        tts.Apply(new TtsSettingsDto { PlaybackRate = 2 });
        var recorder = new Recorder(tts);

        tts.Speak("u1", "Один два три. Четыре пять шесть.");

        Assert.Equal("u1", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        var indices = recorder.Words.Select(w => w.WordIndex).ToArray();
        Assert.Equal(indices.Order(), indices);
        Assert.Equal(0, indices[0]);
        Assert.Equal(5, indices[^1]);
        Assert.Contains(recorder.Words, w => w.Word == "четыре" || w.Word == "Четыре");
    }

    /// <summary>The length of a 16-bit mono WAV from <see cref="WavFile.Mono16"/>.</summary>
    static double WavSeconds(byte[] wav)
    {
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        var sampleRate = BitConverter.ToInt32(wav, 24);
        return (wav.Length - 44) / 2.0 / sampleRate;
    }

    static AvSpeechEngine Silent() => new(new AvSpeechOptions { Language = "ru-RU", Volume = 0 });

    sealed class Recorder
    {
        public Recorder(ITtsEngine tts)
        {
            tts.Progress += (_, args) => Words.Enqueue(args);
            tts.Completed += (_, args) =>
            {
                CompletedIds.Enqueue(args.UtteranceId);
                Completed.TrySetResult(args.UtteranceId);
            };
        }

        public ConcurrentQueue<TtsProgressEventArgs> Words { get; } = new();
        public ConcurrentQueue<string> CompletedIds { get; } = new();
        public TaskCompletionSource<string> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForWordAsync(string utteranceId)
        {
            var deadline = Environment.TickCount64 + 10000;
            while (!Words.Any(w => w.UtteranceId == utteranceId))
            {
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException($"No word progress for {utteranceId}.");
                await Task.Delay(15);
            }
        }
    }
}

sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "Needs AVSpeechSynthesizer.";
    }
}
