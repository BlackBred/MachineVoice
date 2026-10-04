using System.Text.Json.Serialization;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class StoredSubmit
{
    public int Version { get; init; }
    public string Type { get; init; } = "submit";
    public string? Id { get; init; }
    public DateTimeOffset? ReceivedAt { get; init; }
    public string? Source { get; init; }
    public string? Project { get; init; }
    public string? ConversationId { get; init; }
    public string? GenerationId { get; init; }
    public string? Topic { get; init; }
    public string? Text { get; init; }
}

public sealed class IngestResponse
{
    public int Version { get; init; } = ProtocolVersion.Current;
    public string Type { get; init; } = "accepted";
    public string? Id { get; init; }
    public bool Duplicate { get; init; }
    public string? Error { get; init; }

    public static IngestResponse Accepted(string id, bool duplicate) => new()
    {
        Type = "accepted",
        Id = id,
        Duplicate = duplicate,
    };

    public static IngestResponse Rejected(string error) => new()
    {
        Type = "rejected",
        Error = error,
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoredSubmit))]
[JsonSerializable(typeof(IngestResponse))]
sealed partial class IngestJsonContext : JsonSerializerContext;

static class SubmitRules
{
    public const int MaxText = 1_000_000;
    public const int MaxTopic = 4_000;
    public const int MaxSource = 128;
    public const int MaxGenerationId = 256;
    public const int MaxProject = 1_024;
    public const int MaxConversationId = 256;

    public static string? Validate(StoredSubmit submit)
    {
        if (!string.Equals(submit.Type, "submit", StringComparison.Ordinal))
            return ProtocolErrors.UnknownCommand;
        if (submit.Version != ProtocolVersion.Current)
            return ProtocolErrors.UnsupportedVersion;
        if (!Fits(submit.Source, MaxSource, required: true))
            return ProtocolErrors.InvalidArgument;
        if (!Fits(submit.GenerationId, MaxGenerationId, required: true))
            return ProtocolErrors.InvalidArgument;
        if (!Fits(submit.Text, MaxText, required: true))
            return ProtocolErrors.InvalidArgument;
        if (!Fits(submit.Project, MaxProject, required: false))
            return ProtocolErrors.InvalidArgument;
        if (!Fits(submit.ConversationId, MaxConversationId, required: false))
            return ProtocolErrors.InvalidArgument;
        if (!Fits(submit.Topic, MaxTopic, required: false))
            return ProtocolErrors.InvalidArgument;
        return null;
    }

    public static StoredSubmit Canonical(StoredSubmit submit, string id, DateTimeOffset receivedAt) => new()
    {
        Version = ProtocolVersion.Current,
        Type = "submit",
        Id = id,
        ReceivedAt = receivedAt,
        Source = submit.Source!.Trim(),
        Project = BlankToNull(submit.Project),
        ConversationId = BlankToNull(submit.ConversationId),
        GenerationId = submit.GenerationId!.Trim(),
        Topic = BlankToNull(submit.Topic),
        Text = submit.Text!.Trim(),
    };

    static bool Fits(string? value, int max, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
            return !required;
        return value.Trim().Length <= max;
    }

    static string? BlankToNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
