using MachineVoice.Protocol;

namespace MachineVoice.App;

static class Labels
{
    public static string Source(string source) => source switch
    {
        "cursor" => "Cursor",
        "" => "?",
        _ => char.ToUpperInvariant(source[0]) + source[1..],
    };

    /// <summary>"Cursor · MachineVoice": the source and the project.</summary>
    public static string Title(SpeechItemDto item) =>
        string.IsNullOrWhiteSpace(item.Project) ? Source(item.Source) : $"{Source(item.Source)} · {item.Project}";

    public static string Topic(SpeechItemDto item) =>
        string.IsNullOrWhiteSpace(item.Topic) ? "без темы" : item.Topic!;

    /// <summary>One line for menus: title and topic.</summary>
    public static string Line(SpeechItemDto item, int max = 70) => Trim($"{Title(item)} — {Topic(item)}", max);

    public static string Mode(PlaybackMode mode) => mode switch
    {
        PlaybackMode.Auto => "Авто",
        PlaybackMode.Confirm => "По подтверждению",
        PlaybackMode.Silent => "Тихий",
        _ => mode.ToString(),
    };

    public static string Outcome(SpeechOutcome outcome) => outcome switch
    {
        SpeechOutcome.Spoken => "✓",
        SpeechOutcome.Skipped => "↷",
        SpeechOutcome.Stopped => "■",
        _ => "·",
    };

    public static string Player(ControlState state) => state switch
    {
        { Player: PlayerState.Speaking, Current: { } item } => "Читает: " + Line(item, 60),
        { Player: PlayerState.Paused, Current: { } item } => "Пауза: " + Line(item, 60),
        { Confirmation: { } item } => "Ждёт подтверждения: " + Line(item, 50),
        { Holding: true, Queue.Count: > 0 } => $"Остановлено, в очереди {state.Queue.Count}",
        { Mode: PlaybackMode.Silent, Queue.Count: > 0 } => $"Тихий режим, в очереди {state.Queue.Count}",
        _ => "Тишина",
    };

    public static string CursorStatus(SourceConnectionStatus status) => status switch
    {
        SourceConnectionStatus.Connected => "Подключён",
        SourceConnectionStatus.Stale => "Хуки есть, но устарели: подключите заново",
        _ => "Не подключён",
    };

    public static string McpStatus(McpStatusDto status) => status switch
    {
        { Status: SourceConnectionStatus.Connected } => "Подключён",
        { Status: SourceConnectionStatus.Stale } => "Запись устарела: подключите заново",
        { Available: false } => "Недоступен: в этой сборке нет MCP-сервера (соберите приложение через build-app.sh)",
        _ => "Не подключён",
    };

    public static string Trim(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= max ? line : line[..(max - 1)].TrimEnd() + "…";
    }
}
