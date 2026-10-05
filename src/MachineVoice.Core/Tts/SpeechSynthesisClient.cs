using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// The OpenAI-compatible speech endpoint of mlx-audio. The sampling fields are mlx-audio extensions: its own
/// defaults (temperature 0.7, no repetition penalty) make Qwen3-TTS run on long after the text has been read.
/// </summary>
sealed class SpeechSynthesisClient(HttpClient http)
{
    public async Task<byte[]> SynthesizeAsync(QwenTtsSettingsDto settings, string text, CancellationToken cancellationToken)
    {
        var request = new SpeechRequest
        {
            Model = settings.Model,
            Input = text,
            Voice = settings.Voice,
            MaxTokens = MaxTokens(text),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, Url(settings, "/audio/speech"))
        {
            Content = JsonContent.Create(request, SpeechJsonContext.Default.SpeechRequest),
        };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (audio.Length <= WavHeaderSize)
            throw new InvalidDataException("The speech server returned no audio.");
        return audio;
    }

    /// <summary>Loads the model ahead of the first phrase. The first call also downloads it.</summary>
    public async Task LoadModelAsync(QwenTtsSettingsDto settings, CancellationToken cancellationToken)
    {
        var url = Url(settings, "/models") + "?model_name=" + Uri.EscapeDataString(settings.Model);
        using var response = await http.PostAsync(url, content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    const int WavHeaderSize = 44;

    // About one 12.5 Hz codec token per character of speech; the margin covers numbers read out in words.
    static int MaxTokens(string text) => 50 + text.Length * 5 / 2;

    static string Url(QwenTtsSettingsDto settings, string path) => settings.Endpoint.Trim().TrimEnd('/') + path;

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (body.Length > 300)
            body = body[..300];
        throw new HttpRequestException($"Speech server answered {(int)response.StatusCode}: {body}", null, response.StatusCode);
    }
}

sealed class SpeechRequest
{
    public string Model { get; init; } = "";
    public string Input { get; init; } = "";
    public string Voice { get; init; } = "";
    public string ResponseFormat { get; init; } = "wav";
    public double Temperature { get; init; } = 0.9;
    public int TopK { get; init; } = 50;
    public double TopP { get; init; } = 1.0;
    public double RepetitionPenalty { get; init; } = 1.05;
    public int MaxTokens { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SpeechRequest))]
sealed partial class SpeechJsonContext : JsonSerializerContext;
