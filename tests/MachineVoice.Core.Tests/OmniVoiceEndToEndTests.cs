using System.Diagnostics;
using System.Runtime.Versioning;
using MachineVoice.Platform.MacOS;
using MachineVoice.Protocol;
using Xunit.Abstractions;

namespace MachineVoice.Core.Tests;

/// <summary>
/// The real chain: MachineVoice starts mlx_audio.server, OmniVoice makes a random voice, the voice is saved and
/// then cloned sentence by sentence, AVAudioPlayer plays. Needs mlx-audio and the downloaded model:
/// MACHINEVOICE_OMNIVOICE_E2E=1 dotnet test. MACHINEVOICE_QWEN_E2E_VOLUME (0..1, default 0) makes it audible.
/// </summary>
[SupportedOSPlatform("macos")]
public class OmniVoiceEndToEndTests(ITestOutputHelper output)
{
    const int Port = 8897;

    [OmniVoiceFact(Timeout = 600000)]
    public async Task NewVoice_IsSaved_ThenClonedInEverySentence()
    {
        await using var root = new TempRoot();
        var volume = float.TryParse(Environment.GetEnvironmentVariable("MACHINEVOICE_QWEN_E2E_VOLUME"), out var v) ? v : 0;
        var log = new List<string>();
        void Write(string line)
        {
            lock (log)
                log.Add(line);
        }

        string Log()
        {
            lock (log)
                return string.Join("\n", log);
        }

        using var shared = new SharedSpeechServer(new MlxAudioServer(root.Path, Write));
        var library = new VoiceLibrary(Path.Combine(root.Path, VoiceLibrary.DirectoryName), Write);
        var synthesizer = new OmniVoiceSynthesizer(library, shared.Lease(), log: Write);
        var studio = new VoiceStudio(library, synthesizer);
        var settings = new OmniVoiceTtsSettingsDto { Endpoint = $"http://127.0.0.1:{Port}/v1" };

        var clock = Stopwatch.StartNew();
        var draft = await studio.CreateDraftAsync(settings, CancellationToken.None);
        var drafted = clock.Elapsed;
        var voice = studio.Save(draft.Id, "Тест")!;
        Assert.True(new FileInfo(voice.AudioPath).Length > 24000, Log());

        using var tts = OmniVoiceSynthesizer.Engine(new AvAudioPlayer(volume), synthesizer, Write);
        var recorder = new TtsRecorder(tts);
        tts.Apply(new TtsSettingsDto
        {
            Engine = TtsEngineKind.OmniVoice,
            OmniVoice = new OmniVoiceTtsSettingsDto { Endpoint = settings.Endpoint, Voice = voice.Id },
        });

        clock.Restart();
        tts.Speak("e2e", "Привет, это OmniVoice. Проверяю, что MachineVoice читает ответы сохранённым голосом.");
        var finished = await Task.WhenAny(recorder.Completed.Task, recorder.Failed.Task).WaitAsync(TimeSpan.FromMinutes(3));
        var error = recorder.Failed.Task.IsCompleted ? (await recorder.Failed.Task).Error.Message : null;
        Assert.True(finished == recorder.Completed.Task, $"Failed: {error}\n{Log()}");
        Assert.True(recorder.Words.Max(w => w.WordIndex) >= 8, Log());
        output.WriteLine($"Draft in {drafted.TotalSeconds:0.0} s, speech in {clock.Elapsed.TotalSeconds:0.0} s.\n{Log()}");
    }
}

sealed class OmniVoiceFactAttribute : FactAttribute
{
    public OmniVoiceFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("MACHINEVOICE_OMNIVOICE_E2E") != "1")
            Skip = "Set MACHINEVOICE_OMNIVOICE_E2E=1 to run against a real mlx_audio.server.";
    }
}
