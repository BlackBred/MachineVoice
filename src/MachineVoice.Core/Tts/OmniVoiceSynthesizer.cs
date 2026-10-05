using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// OmniVoice through an mlx-audio server. Every chunk clones the saved voice of the settings; without one each
/// chunk gets a voice of its own. mlx-audio estimates the length of the speech from the text, so the tempo is the
/// same for every voice and only the playback rate changes it.
/// <para>
/// Every request costs about two seconds on top of the speech (the model reads the voice sample again), so a short
/// sentence takes longer to synthesize than to play: chunks hold several sentences. The model fills the time it
/// is given from the end, so a clip starts with a click and up to a second of quiet; that is cut to an even pause.
/// </para>
/// </summary>
public sealed class OmniVoiceSynthesizer(VoiceLibrary voices, ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null)
    : MlxSynthesizer<OmniVoiceTtsSettingsDto>(EngineName, TtsEngineKind.OmniVoice, new OmniVoiceTtsSettingsDto(), server, httpHandler, log)
{
    public const string EngineName = "OmniVoice";

    /// <summary>The quiet kept before the speech of a clip: the pause between two clips, which end with the speech.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(250);

    static readonly TimeSpan Click = TimeSpan.FromMilliseconds(40);

    /// <summary>The engine that speaks through <paramref name="synthesizer"/> and disposes it.</summary>
    public static ChunkedAudioEngine Engine(IAudioPlayer player, OmniVoiceSynthesizer synthesizer, Action<string>? log = null) =>
        new(player, synthesizer, EngineName, ChunkTimeout, log, packSentences: true);

    /// <summary>A clip of <paramref name="text"/> in a random voice.</summary>
    public Task<byte[]> SampleAsync(OmniVoiceTtsSettingsDto settings, string text, CancellationToken cancellationToken) =>
        SynthesizeOnceAsync(settings, new SpeechRequest { Model = settings.Model, Input = text, LangCode = settings.Language }, cancellationToken);

    private protected override byte[] Clean(byte[] wav) => WavFile.TrimStart(wav, Lead, Click);

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
