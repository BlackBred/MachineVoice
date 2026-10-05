using System.Net.Sockets;
using System.Runtime.Versioning;
using MachineVoice.Platform.MacOS;
using MachineVoice.Protocol;
using Xunit.Abstractions;

namespace MachineVoice.Core.Tests;

/// <summary>
/// The real chain: MachineVoice starts mlx_audio.server, Qwen3-TTS synthesizes, AVAudioPlayer plays.
/// Needs mlx-audio and the downloaded model, takes about a minute: MACHINEVOICE_QWEN_E2E=1 dotnet test.
/// MACHINEVOICE_QWEN_E2E_VOLUME (0..1, default 0) makes it audible.
/// </summary>
[SupportedOSPlatform("macos")]
public class QwenEndToEndTests(ITestOutputHelper output)
{
    const int Port = 8898;

    [QwenFact(Timeout = 300000)]
    public async Task ManagedServer_SpeaksAndStopsOnDispose()
    {
        await using var root = new TempRoot();
        var volume = float.TryParse(Environment.GetEnvironmentVariable("MACHINEVOICE_QWEN_E2E_VOLUME"), out var v) ? v : 0;
        var log = new List<string>();
        var tts = new NeuralTtsEngine(
            new AvAudioPlayer(volume),
            new MlxAudioServer(root.Path, line => { lock (log) log.Add(line); }),
            log: line => { lock (log) log.Add(line); });
        var recorder = new TtsRecorder(tts);
        tts.Apply(new TtsSettingsDto
        {
            Engine = TtsEngineKind.Qwen,
            Qwen = new QwenTtsSettingsDto { Endpoint = $"http://127.0.0.1:{Port}/v1" },
        });

        var started = DateTime.UtcNow;
        tts.Speak("e2e", "Привет, это Qwen. Проверяю, что MachineVoice читает ответы нейросетью.");
        var first = await WaitForWordAsync(recorder, TimeSpan.FromMinutes(3));
        var latency = DateTime.UtcNow - started;
        var completed = recorder.Completed.Task;
        var failed = recorder.Failed.Task;
        var finished = await Task.WhenAny(completed, failed).WaitAsync(TimeSpan.FromMinutes(2));
        string Log() { lock (log) return string.Join("\n", log); }
        var error = failed.IsCompleted ? (await failed).Error.Message : null;
        Assert.True(finished == completed, $"Failed: {error}\n{Log()}");

        Assert.Equal(0, first.WordIndex);
        Assert.True(recorder.Words.Max(w => w.WordIndex) >= 8, Log());
        output.WriteLine($"First word after {latency.TotalSeconds:0.0} s, total {(DateTime.UtcNow - started).TotalSeconds:0.0} s.\n{Log()}");
        Assert.True(File.Exists(Path.Combine(root.Path, MlxAudioServer.PidFileName)));

        tts.Dispose();
        Assert.False(IsListening(Port), Log());
        Assert.False(File.Exists(Path.Combine(root.Path, MlxAudioServer.PidFileName)));
    }

    static async Task<TtsProgressEventArgs> WaitForWordAsync(TtsRecorder recorder, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (recorder.Words.TryPeek(out var word))
                return word;
            if (recorder.Failed.Task.IsCompleted)
                throw new InvalidOperationException("Qwen failed: " + recorder.Failed.Task.Result.Error);
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("No speech.");
            await Task.Delay(50);
        }
    }

    static bool IsListening(int port)
    {
        using var client = new TcpClient();
        try
        {
            client.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

sealed class QwenFactAttribute : FactAttribute
{
    public QwenFactAttribute()
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("MACHINEVOICE_QWEN_E2E") != "1")
            Skip = "Set MACHINEVOICE_QWEN_E2E=1 to run against a real mlx_audio.server.";
    }
}
