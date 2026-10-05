using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// OmniVoice through an mlx-audio server. Every chunk clones the saved voice of the settings; without one each
/// chunk gets a voice of its own. mlx-audio estimates the length of the speech from the text, so the tempo is the
/// same for every voice and only the playback rate changes it.
/// </summary>
public sealed class OmniVoiceSynthesizer(VoiceLibrary voices, ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null)
    : MlxSynthesizer<OmniVoiceTtsSettingsDto>(EngineName, TtsEngineKind.OmniVoice, new OmniVoiceTtsSettingsDto(), server, httpHandler, log)
{
    public const string EngineName = "OmniVoice";

    /// <summary>The engine that speaks through <paramref name="synthesizer"/> and disposes it.</summary>
    public static ChunkedAudioEngine Engine(IAudioPlayer player, OmniVoiceSynthesizer synthesizer, Action<string>? log = null) =>
        new(player, synthesizer, EngineName, ChunkTimeout, log);

    /// <summary>A clip of <paramref name="text"/> in a random voice.</summary>
    public Task<byte[]> SampleAsync(OmniVoiceTtsSettingsDto settings, string text, CancellationToken cancellationToken) =>
        SynthesizeOnceAsync(settings, new SpeechRequest { Model = settings.Model, Input = text, LangCode = settings.Language }, cancellationToken);

    private protected override OmniVoiceTtsSettingsDto Select(TtsSettingsDto settings) => settings.OmniVoice ?? new OmniVoiceTtsSettingsDto();

    private protected override bool Same(OmniVoiceTtsSettingsDto a, OmniVoiceTtsSettingsDto b) => TtsRules.Same(a, b);

    private protected override SpeechRequest Request(OmniVoiceTtsSettingsDto settings, string text)
    {
        VoiceDto? voice = null;
        if (settings.Voice.Length > 0)
            voice = voices.Find(settings.Voice) ?? throw new FileNotFoundException($"The voice {settings.Voice} is not saved.");

        return new SpeechRequest
        {
            Model = settings.Model,
            Input = text,
            LangCode = settings.Language,
            RefAudio = voice?.AudioPath,
            RefText = voice?.Text,
        };
    }
}
