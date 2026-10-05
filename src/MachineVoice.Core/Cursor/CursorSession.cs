using System.Text.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class CursorTurn
{
    public string? ConversationId { get; set; }
    public string? Project { get; set; }
    public string? Topic { get; set; }
    public string? Text { get; set; }
    public bool Answered { get; set; }
    public bool Completed { get; set; }
}

readonly record struct HookApply(bool Changed, StoredSubmit? Ready)
{
    public static HookApply Unchanged => new(false, null);
    public static HookApply Stored => new(true, null);
    public static HookApply ReadyNow(StoredSubmit submit) => new(true, submit);
}

/// <summary>
/// Folds Cursor hook events into one speech item per generation.
/// beforeSubmitPrompt stores the prompt as the topic, afterAgentResponse stores the text,
/// and stop with status completed queues the item once both are known.
/// Cursor sometimes sends afterAgentResponse with empty text; such a turn is queued as <see cref="MissingText"/>.
/// </summary>
sealed class CursorSession
{
    public const string MissingText = "Ответ готов, но получить его текст из Cursor не удалось.";

    public Dictionary<string, CursorTurn> Turns { get; } = new(StringComparer.Ordinal);

    public static string? GenerationId(JsonElement payload)
    {
        var generation = Field(payload, "generation_id")?.Trim();
        if (string.IsNullOrEmpty(generation) || generation.Length > SubmitRules.MaxGenerationId)
            return null;
        return generation;
    }

    /// <summary>
    /// The chat the user has just written to. Its queued answers are already read or no longer relevant.
    /// </summary>
    public static string? RepliedConversation(JsonElement payload) =>
        string.Equals(Field(payload, "hook_event_name"), "beforeSubmitPrompt", StringComparison.Ordinal)
            ? Limit(Field(payload, "conversation_id"), SubmitRules.MaxConversationId)
            : null;

    public HookApply Apply(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return HookApply.Unchanged;

        var generation = GenerationId(payload);
        if (generation is null)
            return HookApply.Unchanged;

        switch (Field(payload, "hook_event_name"))
        {
            case "beforeSubmitPrompt":
                var prompted = Turn(generation);
                Fill(prompted, payload);
                prompted.Topic = Topic(Field(payload, "prompt"));
                return HookApply.Stored;
            case "afterAgentResponse":
                var answered = Turn(generation);
                Fill(answered, payload);
                var text = Limit(Field(payload, "text"), SubmitRules.MaxText);
                if (text is not null)
                    answered.Text = text;
                answered.Answered = true;
                return IsReady(answered)
                    ? HookApply.ReadyNow(ToSubmit(generation, answered))
                    : HookApply.Stored;
            case "stop":
                if (!string.Equals(Field(payload, "status"), "completed", StringComparison.Ordinal))
                    return Turns.Remove(generation) ? HookApply.Stored : HookApply.Unchanged;

                var stopped = Turn(generation);
                Fill(stopped, payload);
                stopped.Completed = true;
                return IsReady(stopped)
                    ? HookApply.ReadyNow(ToSubmit(generation, stopped))
                    : HookApply.Stored;
            default:
                return HookApply.Unchanged;
        }
    }

    public List<StoredSubmit> ReadySubmits()
    {
        var ready = new List<StoredSubmit>();
        foreach (var (generation, turn) in Turns)
        {
            if (IsReady(turn))
                ready.Add(ToSubmit(generation, turn));
        }

        return ready;
    }

    CursorTurn Turn(string generation)
    {
        if (!Turns.TryGetValue(generation, out var turn))
        {
            turn = new CursorTurn();
            Turns[generation] = turn;
        }

        return turn;
    }

    static void Fill(CursorTurn turn, JsonElement payload)
    {
        var conversation = Limit(Field(payload, "conversation_id"), SubmitRules.MaxConversationId);
        if (conversation is not null)
            turn.ConversationId = conversation;

        var project = Project(payload);
        if (project is not null)
            turn.Project = project;
    }

    static StoredSubmit ToSubmit(string generationId, CursorTurn turn) => new()
    {
        Version = ProtocolVersion.Current,
        Type = "submit",
        Source = CursorHooks.SourceName,
        Project = turn.Project,
        ConversationId = turn.ConversationId,
        GenerationId = generationId,
        Topic = turn.Topic,
        Text = HasText(turn) ? turn.Text : MissingText,
    };

    static string? Project(JsonElement payload)
    {
        if (!payload.TryGetProperty("workspace_roots", out var roots) || roots.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var root in roots.EnumerateArray())
        {
            if (root.ValueKind != JsonValueKind.String)
                continue;
            var path = root.GetString();
            if (string.IsNullOrWhiteSpace(path))
                continue;
            var name = Path.GetFileName(path.TrimEnd('/', '\\'));
            if (!string.IsNullOrEmpty(name))
                return Limit(name, SubmitRules.MaxProject);
        }

        return null;
    }

    static string? Topic(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;
        var collapsed = string.Join(' ', prompt.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return Limit(collapsed, SubmitRules.MaxTopic);
    }

    static string? Field(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    static string? Limit(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    static bool IsReady(CursorTurn turn) => turn.Completed && (turn.Answered || HasText(turn));

    static bool HasText(CursorTurn turn) => !string.IsNullOrWhiteSpace(turn.Text);
}
