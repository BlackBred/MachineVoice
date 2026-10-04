using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Retells the response through an OpenAI-compatible chat/completions endpoint.
/// On timeout, HTTP error or an empty answer it returns the input unchanged, so the rules handle the original text.
/// </summary>
public sealed partial class LlmSummarizer : ITextProcessor
{
    public const string SystemPrompt =
        "Перескажи ответ AI-ассистента так, чтобы его было удобно слушать. " +
        "Пиши на языке ответа. Сохрани суть, выводы, найденные проблемы и вопросы к пользователю. " +
        "Не зачитывай код, пути к файлам, ссылки и таблицы дословно: скажи своими словами, что в них. " +
        "Без Markdown, списков, заголовков и эмодзи, короткими предложениями. Ничего не добавляй от себя.";

    // Hybrid reasoning models (Qwen3) skip the chain of thought on this switch; other models ignore it.
    // A server-specific request field would make strict OpenAI-compatible endpoints reject the request.
    const string NoThinking = "\n/no_think";

    /// <summary>A longer retelling is cut off mid-sentence, so the rules are used instead.</summary>
    public const int MaxTokens = 1024;

    readonly HttpClient _http;
    readonly SummarySettingsDto _settings;
    readonly Action<string>? _log;

    public LlmSummarizer(HttpClient http, SummarySettingsDto settings, Action<string>? log = null)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    public static bool IsUsable(SummarySettingsDto settings) =>
        settings.Enabled && SummaryRules.Validate(settings) is null;

    public async ValueTask<string> ProcessAsync(string text, SpeechContext context, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        try
        {
            var summary = await RequestAsync(text, context, timeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(summary))
                return summary;
            _log?.Invoke("Summary is empty, using the rules.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke($"Summary timed out after {_settings.TimeoutSeconds} s, using the rules.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException or InvalidDataException)
        {
            _log?.Invoke($"Summary failed, using the rules: {ex.Message}");
        }

        return text;
    }

    async Task<string?> RequestAsync(string text, SpeechContext context, CancellationToken cancellationToken)
    {
        var request = new ChatRequest
        {
            Model = _settings.Model.Trim(),
            Messages =
            [
                new ChatMessage { Role = "system", Content = SystemPrompt + NoThinking },
                new ChatMessage { Role = "user", Content = UserMessage(text, context) },
            ],
            MaxTokens = MaxTokens,
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint.Trim().TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(request, SummaryJsonContext.Default.ChatRequest),
        };
        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync(SummaryJsonContext.Default.ChatResponse, cancellationToken).ConfigureAwait(false);
        var choice = body?.Choices?.FirstOrDefault();
        if (choice?.FinishReason == "length")
            throw new InvalidDataException($"the answer exceeded {MaxTokens} tokens");
        var content = choice?.Message?.Content;
        return content is null ? null : Thinking().Replace(content, "").Trim();
    }

    static string UserMessage(string text, SpeechContext context) =>
        string.IsNullOrWhiteSpace(context.Topic)
            ? "Ответ:\n" + text
            : "Вопрос пользователя: " + context.Topic.Trim() + "\n\nОтвет:\n" + text;

    // Reasoning models served by Ollama put their chain of thought into the content.
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex Thinking();
}

static class SummaryRules
{
    public const int MaxModel = 256;
    public const int MaxApiKey = 4_096;

    public static string? Validate(SummarySettingsDto settings)
    {
        if (!Uri.TryCreate(settings.Endpoint?.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            return ProtocolErrors.InvalidArgument;
        if (settings.TimeoutSeconds is < 1 or > SummarySettingsDto.MaxTimeoutSeconds)
            return ProtocolErrors.InvalidArgument;
        if ((settings.Model?.Trim().Length ?? 0) > MaxModel || (settings.ApiKey?.Length ?? 0) > MaxApiKey)
            return ProtocolErrors.InvalidArgument;
        if (settings.Enabled && string.IsNullOrWhiteSpace(settings.Model))
            return ProtocolErrors.InvalidArgument;
        return null;
    }

    public static SummarySettingsDto Canonical(SummarySettingsDto settings) => new()
    {
        Enabled = settings.Enabled,
        Endpoint = settings.Endpoint.Trim(),
        Model = settings.Model?.Trim() ?? "",
        ApiKey = string.IsNullOrWhiteSpace(settings.ApiKey) ? null : settings.ApiKey.Trim(),
        TimeoutSeconds = settings.TimeoutSeconds,
    };
}

sealed class ChatRequest
{
    public string Model { get; init; } = "";
    public List<ChatMessage> Messages { get; init; } = [];
    public double Temperature { get; init; } = 0.3;
    public int MaxTokens { get; init; }
    public bool Stream { get; init; }
}

sealed class ChatMessage
{
    public string? Role { get; init; }
    public string? Content { get; init; }
}

sealed class ChatResponse
{
    public List<ChatChoice>? Choices { get; init; }
}

sealed class ChatChoice
{
    public ChatMessage? Message { get; init; }
    public string? FinishReason { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(ChatResponse))]
sealed partial class SummaryJsonContext : JsonSerializerContext;
