using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>The OpenAI-compatible speech endpoint of mlx-audio and its model management.</summary>
sealed class SpeechSynthesisClient(HttpClient http)
{
    public async Task<byte[]> SynthesizeAsync(IMlxModelSettings settings, SpeechRequest request, CancellationToken cancellationToken)
    {
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
    public async Task LoadModelAsync(IMlxModelSettings settings, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(ModelUrl(settings), content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Frees the model's memory in a server that keeps running for another model.</summary>
    public async Task UnloadModelAsync(IMlxModelSettings settings, CancellationToken cancellationToken)
    {
        using var response = await http.DeleteAsync(ModelUrl(settings), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    const int WavHeaderSize = 44;

    static string ModelUrl(IMlxModelSettings settings) =>
        Url(settings, "/models") + "?model_name=" + Uri.EscapeDataString(settings.Model);

    static string Url(IMlxModelSettings settings, string path) => settings.Endpoint.Trim().TrimEnd('/') + path;

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

/// <summary>
/// Fields beyond the OpenAI ones are mlx-audio extensions; each model reads the ones it knows. Unset fields are
/// left out and keep the server defaults.
/// </summary>
sealed class SpeechRequest
{
    public string Model { get; init; } = "";
    public string Input { get; init; } = "";
    public string? Voice { get; init; }
    public string ResponseFormat { get; init; } = "wav";
    public string? LangCode { get; init; }

    /// <summary>A WAV file on the server's machine and its transcript: the voice to clone.</summary>
    public string? RefAudio { get; init; }

    public string? RefText { get; init; }
    public double? Temperature { get; init; }
    public int? TopK { get; init; }
    public double? TopP { get; init; }
    public double? RepetitionPenalty { get; init; }
    public int? MaxTokens { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SpeechRequest))]
sealed partial class SpeechJsonContext : JsonSerializerContext;
