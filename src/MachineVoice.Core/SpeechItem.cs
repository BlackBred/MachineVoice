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
