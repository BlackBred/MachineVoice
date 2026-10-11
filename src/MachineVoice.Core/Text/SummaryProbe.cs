using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Asks an OpenAI-compatible endpoint for its model list. A chat request is not sent: that would load the model.
/// </summary>
static class SummaryProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<SummaryProbeDto> CheckAsync(
        HttpClient http, string endpoint, string model, string? apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, endpoint.Trim().TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await http.SendAsync(message, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new SummaryProbeDto { Status = SummaryProbeStatus.Unauthorized };
            if (!response.IsSuccessStatusCode)
                return new SummaryProbeDto { Status = SummaryProbeStatus.Unreachable };

            var body = await response.Content.ReadFromJsonAsync(SummaryJsonContext.Default.ModelList, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(model))
                return new SummaryProbeDto { Status = SummaryProbeStatus.Reachable };
            var wanted = model.Trim();
            var found = body?.Data?.Any(item => Listed(item.Id, wanted)) == true;
            return new SummaryProbeDto { Status = found ? SummaryProbeStatus.Available : SummaryProbeStatus.Missing };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SummaryProbeDto { Status = SummaryProbeStatus.Unreachable };
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            return new SummaryProbeDto { Status = SummaryProbeStatus.Unreachable };
        }
    }

    /// <summary>Ollama lists a model pulled without a tag as <c>name:latest</c>.</summary>
    static bool Listed(string? id, string model) =>
        id is not null && (
            string.Equals(id, model, StringComparison.Ordinal)
            || string.Equals(id, model + ":latest", StringComparison.Ordinal)
            || string.Equals(model, id + ":latest", StringComparison.Ordinal));
}

sealed class ModelList
{
    public List<ModelInfo>? Data { get; init; }
}

sealed class ModelInfo
{
    public string? Id { get; init; }
}
