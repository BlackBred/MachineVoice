namespace MachineVoice.Core;

public sealed class SpeechItem
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public string? Project { get; init; }
    public string? ConversationId { get; init; }
    public required string GenerationId { get; init; }
    public string? Topic { get; init; }
    public required string Text { get; init; }

    /// <summary>The chosen text, after the rule steps. The item is spoken only after it is set.</summary>
    public string? Speech { get; internal set; }

    /// <summary>The original text after the rule steps. Set together with <see cref="Speech"/>.</summary>
    public string? FullSpeech { get; internal set; }

    /// <summary>The retelling after the rule steps. Null when none was made.</summary>
    public string? SummarySpeech { get; internal set; }

    /// <summary>True when <see cref="Speech"/> is the retelling.</summary>
    public bool ReadSummary { get; internal set; }

    public required DateTimeOffset ReceivedAt { get; init; }
    internal string InboxPath { get; init; } = "";
}

public sealed record SpeechDraft(
    string Source,
    string GenerationId,
    string Text,
    string? Project = null,
    string? ConversationId = null,
    string? Topic = null);
