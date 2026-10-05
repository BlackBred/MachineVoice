using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Qwen3-TTS through an mlx-audio server. The model has no speed control of its own (mlx-audio ignores
/// <c>speed</c> for it), so only the playback rate changes its tempo.
/// </summary>
public sealed class QwenSynthesizer(ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null)
    : MlxSynthesizer<QwenTtsSettingsDto>(EngineName, TtsEngineKind.Qwen, new QwenTtsSettingsDto(), server, httpHandler, log)
{
    public const string EngineName = "Qwen3-TTS";

    /// <summary>The engine that speaks through this synthesizer.</summary>
    public static ChunkedAudioEngine Engine(IAudioPlayer player, ISpeechServer? server = null, HttpMessageHandler? httpHandler = null, Action<string>? log = null) =>
        new(player, new QwenSynthesizer(server, httpHandler, log), EngineName, ChunkTimeout, log);

    private protected override QwenTtsSettingsDto Select(TtsSettingsDto settings) => settings.Qwen ?? new QwenTtsSettingsDto();

    private protected override bool Same(QwenTtsSettingsDto a, QwenTtsSettingsDto b) => TtsRules.Same(a, b);

    // mlx-audio's own sampling defaults (temperature 0.7, no repetition penalty) make Qwen3-TTS run on long after
    // the text has been read.
    private protected override SpeechRequest Request(QwenTtsSettingsDto settings, string text) => new()
    {
        Model = settings.Model,
        Input = text,
        Voice = settings.Voice,
        Temperature = 0.9,
        TopK = 50,
        TopP = 1.0,
        RepetitionPenalty = 1.05,
        MaxTokens = MaxTokens(text),
    };

    // About one 12.5 Hz codec token per character of speech; the margin covers numbers read out in words.
    static int MaxTokens(string text) => 50 + text.Length * 5 / 2;
}
