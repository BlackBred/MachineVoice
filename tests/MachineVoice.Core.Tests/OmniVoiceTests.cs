using System.Net;
using System.Text;
using System.Text.Json;
using MachineVoice.Core;
using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

public class OmniVoiceEngineTests
{
    [Fact(Timeout = 20000)]
    public async Task Speak_ClonesTheSavedVoice_InEveryChunk()
    {
        await using var root = new TempRoot();
        var library = new VoiceLibrary(Path.Combine(root.Path, "voices"));
        var voice = library.Save(library.AddDraft(Clip(), "Образец голоса.").Id, "Тест")!;
        var player = new FakeAudioPlayer();
        var http = new StubHttpHandler((_, body, _) => Task.FromResult(Audio(body)));
        using var tts = OmniVoiceSynthesizer.Engine(player, new OmniVoiceSynthesizer(library, httpHandler: http));
        var recorder = new TtsRecorder(tts);
        tts.Apply(Settings(voice.Id));

        tts.Speak("u", "Первая фраза. Вторая фраза.");
        for (var i = 1; i <= 2; i++)
        {
            await player.WaitForPlayAsync(i);
            player.End();
        }

        Assert.Equal("u", await recorder.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var speech = SpeechBodies(http);
        Assert.Equal(new[] { "Первая фраза.", "Вторая фраза." }, speech.Select(body => Field(body, "input")));
        Assert.All(speech, body =>
        {
            Assert.Equal(OmniVoiceTtsSettingsDto.DefaultModel, Field(body, "model"));
            Assert.Equal("ru", Field(body, "lang_code"));
            Assert.Equal(voice.AudioPath, Field(body, "ref_audio"));
            Assert.Equal("Образец голоса.", Field(body, "ref_text"));
            Assert.Null(Field(body, "voice"));
            Assert.Null(Field(body, "temperature"));
        });
    }

    [Fact(Timeout = 20000)]
    public async Task Speak_WithoutAVoice_SendsNoSample()
    {
        await using var root = new TempRoot();
        var player = new FakeAudioPlayer();
        var http = new StubHttpHandler((_, body, _) => Task.FromResult(Audio(body)));
        using var tts = OmniVoiceSynthesizer.Engine(player, new OmniVoiceSynthesizer(new VoiceLibrary(root.Path), httpHandler: http));
        tts.Apply(Settings(""));

        tts.Speak("u", "Фраза.");
        await player.WaitForPlayAsync(1);

        var body = Assert.Single(SpeechBodies(http));
        Assert.Null(Field(body, "ref_audio"));
        Assert.Null(Field(body, "ref_text"));
    }

    [Fact(Timeout = 20000)]
    public async Task Speak_WithAVoiceThatIsGone_FailsWithTheWholeText()
    {
        await using var root = new TempRoot();
        var http = new StubHttpHandler((_, body, _) => Task.FromResult(Audio(body)));
        using var tts = OmniVoiceSynthesizer.Engine(new FakeAudioPlayer(), new OmniVoiceSynthesizer(new VoiceLibrary(root.Path), httpHandler: http));
        var recorder = new TtsRecorder(tts);
        tts.Apply(Settings("abc123"));

        tts.Speak("u", "Раз. Два.");

        var failed = await recorder.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Раз. Два.", failed.RemainingText);
        Assert.Equal(0, failed.WordOffset);
        Assert.Empty(SpeechBodies(http));
    }

    [Fact(Timeout = 20000)]
    public async Task LeavingTheEngine_UnloadsItsModel_FromTheServer()
    {
        var server = new FakeSpeechServer();
        var http = new StubHttpHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var tts = QwenSynthesizer.Engine(new FakeAudioPlayer(), server, http);

        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.Qwen });
        await WaitAsync(() => server.Calls.Contains("idle 10"));
        tts.Apply(new TtsSettingsDto { Engine = TtsEngineKind.OmniVoice });

        await WaitAsync(() => http.Requests.Any(r => r.Request.Method == HttpMethod.Delete));
        var unload = http.Requests.Single(r => r.Request.Method == HttpMethod.Delete).Request;
        Assert.Equal(
            "http://127.0.0.1:8899/v1/models?model_name=" + Uri.EscapeDataString(QwenTtsSettingsDto.DefaultModel),
            unload.RequestUri!.ToString());
    }

    [Fact(Timeout = 20000)]
    public async Task NewDraft_StartsTheServer_LoadsTheModel_AndKeepsTheClip()
    {
        await using var root = new TempRoot();
        var server = new FakeSpeechServer();
        var http = new StubHttpHandler((request, body, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/audio/speech") ? Audio(body) : new HttpResponseMessage(HttpStatusCode.OK)));
        var library = new VoiceLibrary(Path.Combine(root.Path, "voices"));
        using var synthesizer = new OmniVoiceSynthesizer(library, server, http);
        var studio = new VoiceStudio(library, synthesizer);

        var draft = await studio.CreateDraftAsync(new OmniVoiceTtsSettingsDto { Endpoint = "http://tts.test/v1", UnloadAfterMinutes = 3 }, CancellationToken.None);

        Assert.Equal("", draft.Name);
        Assert.Equal(VoiceStudio.SampleText, draft.Text);
        Assert.True(File.Exists(draft.AudioPath));
        Assert.Empty(studio.List());
        Assert.Equal(new[] { "busy", "ensure http://tts.test/v1", "idle 3" }, server.Calls);
        Assert.Equal(
            new[] { "http://tts.test/v1/models?model_name=" + Uri.EscapeDataString(OmniVoiceTtsSettingsDto.DefaultModel), "http://tts.test/v1/audio/speech" },
            http.Requests.Select(r => r.Request.RequestUri!.ToString()));
        var body = http.Requests.Last().Body;
        Assert.Equal(VoiceStudio.SampleText, Field(body, "input"));
        Assert.Null(Field(body, "ref_audio"));
    }

    internal static TtsSettingsDto Settings(string voice) => new()
    {
        Engine = TtsEngineKind.OmniVoice,
        OmniVoice = new OmniVoiceTtsSettingsDto { Endpoint = "http://tts.test/v1", Voice = voice },
    };

    internal static byte[] Clip() => Encoding.UTF8.GetBytes(new string('w', 64));

    static HttpResponseMessage Audio(string body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Encoding.UTF8.GetBytes(new string('w', 64) + body)),
    };

    static List<string> SpeechBodies(StubHttpHandler http) =>
        http.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/audio/speech")).Select(r => r.Body).ToList();

    static string? Field(string body, string name)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty(name, out var value) ? value.ToString() : null;
    }

    static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException();
            await Task.Delay(10);
        }
    }
}

public class SharedSpeechServerTests
{
    [Fact]
    public void Server_IsBusyWhileAnyLeaseIs_ThenIdleForTheLongestDelay()
    {
        var inner = new FakeSpeechServer();
        var shared = new SharedSpeechServer(inner);
        var qwen = shared.Lease();
        var omni = shared.Lease();
        var unused = shared.Lease();

        qwen.Busy();
        omni.Idle(TimeSpan.FromMinutes(5));
        qwen.Idle(TimeSpan.FromMinutes(10));
        omni.Idle(null);
        omni.Dispose();
        unused.Dispose();
        shared.Dispose();

        Assert.Equal(new[] { "busy", "busy", "idle 10", "idle never", "idle 10", "idle 10", "dispose" }, inner.Calls);
    }
}

public class VoiceLibraryTests
{
    [Fact]
    public async Task Drafts_BecomeVoicesWhenSaved_AndDraftsOfAnEarlierRunAreDeleted()
    {
        await using var root = new TempRoot();
        var directory = Path.Combine(root.Path, "voices");
        var library = new VoiceLibrary(directory);
        var kept = library.AddDraft(OmniVoiceEngineTests.Clip(), "Первый.");
        var dropped = library.AddDraft(OmniVoiceEngineTests.Clip(), "Второй.");
        Assert.Empty(library.List());
        Assert.Null(library.Find(kept.Id));

        var saved = library.Save(kept.Id, "  Бас  ")!;
        Assert.Equal("Бас", saved.Name);
        Assert.Equal("Первый.", saved.Text);
        Assert.Equal(Path.Combine(directory, kept.Id + ".wav"), saved.AudioPath);
        Assert.True(File.Exists(saved.AudioPath));
        Assert.False(File.Exists(kept.AudioPath));
        Assert.Null(library.Save(kept.Id, "Снова"));

        var restarted = new VoiceLibrary(directory);
        Assert.False(File.Exists(dropped.AudioPath));
        Assert.Null(restarted.Save(dropped.Id, "Поздно"));
        Assert.Equal("Бас", Assert.Single(restarted.List()).Name);

        Assert.Null(restarted.Find("../" + kept.Id));
        Assert.False(restarted.Delete(""));
        Assert.True(restarted.Delete(kept.Id));
        Assert.False(File.Exists(saved.AudioPath));
        Assert.Empty(restarted.List());
    }
}

public class VoiceCommandsTests
{
    [Fact(Timeout = 20000)]
    public async Task Voices_AreCreatedSavedAndDeleted_ThroughTheProtocol()
    {
        await using var root = new TempRoot();
        var studio = new FakeStudio(new VoiceLibrary(Path.Combine(root.Path, "voices")));
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine(), voices: studio);
        await using var client = await host.ConnectInProcessAsync();
        var log = EventLog.Pump(client);
        await log.TakeAsync<SnapshotEvent>();

        Assert.True((await client.UpdateSettingsAsync(tts: new TtsSettingsDto
        {
            OmniVoice = new OmniVoiceTtsSettingsDto { Endpoint = "http://tts.test/v1" },
        })).Ok);
        var created = await client.CreateVoiceAsync();
        Assert.True(created.Ok);
        Assert.Equal("http://tts.test/v1", studio.Settings!.Endpoint);
        var draft = created.Voice!;

        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.SaveVoiceAsync(draft.Id, " ")).Error);
        Assert.Equal(ProtocolErrors.InvalidArgument, (await client.SaveVoiceAsync(draft.Id, new string('я', 65))).Error);
        Assert.Equal(ProtocolErrors.NotFound, (await client.SaveVoiceAsync("nosuchdraft", "Голос")).Error);
        var saved = await client.SaveVoiceAsync(draft.Id, "Голос");
        Assert.True(saved.Ok);
        Assert.Equal(draft.Id, Assert.Single(saved.Voices!).Id);
        Assert.Equal("Голос", Assert.Single((await client.ListVoicesAsync()).Voices!).Name);

        Assert.True((await client.UpdateSettingsAsync(tts: OmniVoiceEngineTests.Settings(draft.Id))).Ok);
        await log.TakeAsync<SettingsChangedEvent>();
        await log.TakeAsync<SettingsChangedEvent>();
        var deleted = await client.DeleteVoiceAsync(draft.Id);
        Assert.True(deleted.Ok);
        Assert.Empty(deleted.Voices!);
        var reset = await log.TakeAsync<SettingsChangedEvent>();
        Assert.Equal("", reset.Settings.Tts.OmniVoice.Voice);
        Assert.Equal(TtsEngineKind.OmniVoice, reset.Settings.Tts.Engine);
        Assert.Equal(ProtocolErrors.NotFound, (await client.DeleteVoiceAsync(draft.Id)).Error);

        studio.Error = new HttpRequestException("connection refused");
        var failed = await client.CreateVoiceAsync();
        Assert.Equal(ProtocolErrors.Unavailable, failed.Error);
        Assert.Equal("connection refused", failed.Detail);
        await host.DisposeAsync();
    }

    [Fact(Timeout = 20000)]
    public async Task WithoutAStudio_VoiceCommandsAreRejected_AndBadOmniVoiceSettingsToo()
    {
        await using var root = new TempRoot();
        var host = await TestHost.StartAsync(root.Path, new ManualTtsEngine());
        await using var client = await host.ConnectInProcessAsync();

        Assert.Equal(ProtocolErrors.InvalidState, (await client.ListVoicesAsync()).Error);
        Assert.Equal(ProtocolErrors.InvalidState, (await client.CreateVoiceAsync()).Error);
        foreach (var omni in new[]
        {
            new OmniVoiceTtsSettingsDto { Language = "r u" },
            new OmniVoiceTtsSettingsDto { Language = "" },
            new OmniVoiceTtsSettingsDto { Voice = new string('a', 65) },
            new OmniVoiceTtsSettingsDto { Endpoint = "file:///tmp" },
            new OmniVoiceTtsSettingsDto { UnloadAfterMinutes = -1 },
        })
        {
            var rejected = await client.UpdateSettingsAsync(tts: new TtsSettingsDto { Engine = TtsEngineKind.OmniVoice, OmniVoice = omni });
            Assert.Equal(ProtocolErrors.InvalidArgument, rejected.Error);
        }

        var accepted = await client.UpdateSettingsAsync(tts: new TtsSettingsDto { OmniVoice = new OmniVoiceTtsSettingsDto { Language = " EN " } });
        Assert.True(accepted.Ok);
        Assert.Equal("en", (await client.GetSettingsAsync()).Settings!.Tts.OmniVoice.Language);
        await host.DisposeAsync();
    }

    sealed class FakeStudio(VoiceLibrary library) : IVoiceStudio
    {
        public OmniVoiceTtsSettingsDto? Settings { get; private set; }
        public Exception? Error { get; set; }

        public IReadOnlyList<VoiceDto> List() => library.List();

        public async Task<VoiceDto> CreateDraftAsync(OmniVoiceTtsSettingsDto settings, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            Settings = settings;
            if (Error is { } error)
                throw error;
            return library.AddDraft(OmniVoiceEngineTests.Clip(), "Образец.");
        }

        public VoiceDto? Save(string draftId, string name) => library.Save(draftId, name);

        public bool Delete(string voiceId) => library.Delete(voiceId);
    }
}
