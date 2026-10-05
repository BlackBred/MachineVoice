namespace MachineVoice.Protocol;

/// <summary>
/// Client-side mirror of the daemon state: a snapshot kept current by the event stream.
/// Not thread-safe; apply events from one thread.
/// </summary>
public sealed class ControlState
{
    public const int HistoryLimit = 50;

    // Items leave the queue before player.state names them, so the mirror keeps every live item by id.
    readonly Dictionary<string, SpeechItemDto> _live = new(StringComparer.Ordinal);
    readonly List<HistoryEntryDto> _history = [];
    readonly Dictionary<string, SourceStatusDto> _sources = new(StringComparer.Ordinal);

    public PlayerState Player { get; private set; }
    public SpeechItemDto? Current { get; private set; }
    public PlaybackMode Mode { get; private set; }

    /// <summary>Stop holds the queue until resume. Derived from events, exact after every snapshot.</summary>
    public bool Holding { get; private set; }

    public SpeechItemDto? Confirmation { get; private set; }
    public IReadOnlyList<SpeechItemDto> Queue { get; private set; } = [];

    /// <summary>
    /// <see cref="Queue"/> without the item on screen: the one awaiting confirmation stays at the head of the queue.
    /// </summary>
    public IEnumerable<SpeechItemDto> Waiting
    {
        get
        {
            var shownId = Current?.Id ?? Confirmation?.Id;
            return Queue.Where(item => item.Id != shownId);
        }
    }

    /// <summary>Oldest first, at most <see cref="HistoryLimit"/> entries.</summary>
    public IReadOnlyList<HistoryEntryDto> History => _history;

    public SettingsDto Settings { get; private set; } = new();
    public IReadOnlyDictionary<string, SourceStatusDto> Sources => _sources;
    public McpStatusDto? Mcp { get; private set; }

    /// <summary>Index of the word being spoken in <see cref="Current"/>, or -1.</summary>
    public int WordIndex { get; private set; } = -1;

    /// <summary>Seconds of audio played in <see cref="Current"/>.</summary>
    public double Position { get; private set; }

    /// <summary>Seconds of audio in <see cref="Current"/>, partly estimated; 0 when the engine cannot tell.</summary>
    public double Duration { get; private set; }

    /// <summary>An event referred to an item this mirror does not know; take a new snapshot.</summary>
    public bool NeedsSnapshot { get; private set; }

    public void Reset(SnapshotDto snapshot)
    {
        _live.Clear();
        Player = snapshot.Player;
        Current = snapshot.Current;
        Mode = snapshot.Mode;
        Holding = snapshot.Holding;
        Confirmation = snapshot.Confirmation;
        Queue = snapshot.Queue.ToList();
        _history.Clear();
        _history.AddRange(snapshot.History.TakeLast(HistoryLimit));
        Settings = snapshot.Settings;
        _sources.Clear();
        foreach (var source in snapshot.Sources)
            _sources[source.Name] = source;
        ClearProgress();
        NeedsSnapshot = false;

        foreach (var item in Queue)
            _live[item.Id] = item;
        if (Current is not null)
            _live[Current.Id] = Current;
        if (Confirmation is not null)
            _live[Confirmation.Id] = Confirmation;
    }

    /// <summary>Applies one event. Returns false for events that do not change the state.</summary>
    public bool Apply(EventMessage message)
    {
        switch (message)
        {
            case SnapshotEvent snapshot:
                Reset(snapshot.Snapshot);
                return true;
            case PlayerStateEvent state:
                OnPlayer(state);
                return true;
            case PlayerProgressEvent progress:
                if (Current?.Id != progress.ItemId)
                    return false;
                WordIndex = progress.WordIndex;
                return true;
            case PlayerPositionEvent position:
                if (Current?.Id != position.ItemId)
                    return false;
                Position = position.Position;
                Duration = position.Duration;
                return true;
            case QueueChangedEvent queue:
                OnQueue(queue.Items);
                return true;
            case HistoryAppendedEvent history:
                OnHistory(history.Entry);
                return true;
            case ConfirmationRequestedEvent confirmation:
                Confirmation = confirmation.Item;
                _live[confirmation.Item.Id] = confirmation.Item;
                return true;
            case ConfirmationClearedEvent cleared:
                if (Confirmation?.Id != cleared.ItemId)
                    return false;
                Confirmation = null;
                return true;
            case SettingsChangedEvent settings:
                Settings = settings.Settings;

                // The core releases a stop only when the mode changes.
                if (Mode != settings.Settings.Mode)
                    Holding = false;
                Mode = settings.Settings.Mode;
                return true;
            case SourceChangedEvent source:
                _sources.TryGetValue(source.Source, out var previous);
                _sources[source.Source] = new SourceStatusDto
                {
                    Name = source.Source,
                    Status = source.Status,
                    LegacySpeaker = previous?.LegacySpeaker ?? false,
                };
                return true;
            case McpChangedEvent mcp:
                Mcp = mcp.Mcp;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Stores a status the client asked for, so it does not wait for the next event.</summary>
    public void SetSource(SourceStatusDto source) => _sources[source.Name] = source;

    public void SetMcp(McpStatusDto mcp) => Mcp = mcp;

    /// <summary>Total number of words in the current speech, for a progress estimate.</summary>
    public int CurrentWordCount => Current is null ? 0 : CountWords(Current.Speech ?? Current.Text);

    /// <summary>
    /// 0..1, or null when nothing is being read. By time when the engine reports it, otherwise by words.
    /// <see cref="Position"/> is <see cref="Progress"/> times <see cref="Duration"/>, so a click on a progress bar maps to a seek.
    /// </summary>
    public double? Progress
    {
        get
        {
            if (Current is null)
                return null;
            if (Duration > 0)
                return Math.Clamp(Position / Duration, 0, 1);
            var total = CurrentWordCount;
            if (total == 0)
                return null;
            return Math.Clamp((WordIndex + 1) / (double)total, 0, 1);
        }
    }

    void ClearProgress()
    {
        WordIndex = -1;
        Position = 0;
        Duration = 0;
    }

    void OnPlayer(PlayerStateEvent state)
    {
        Player = state.State;
        if (state.State == PlayerState.Idle || state.ItemId is null)
        {
            Current = null;
            ClearProgress();
            return;
        }

        if (state.State == PlayerState.Speaking)
            Holding = false;
        if (Current?.Id == state.ItemId)
            return;

        ClearProgress();
        if (_live.TryGetValue(state.ItemId, out var item))
        {
            Current = item;
            return;
        }

        Current = null;
        NeedsSnapshot = true;
    }

    void OnQueue(List<SpeechItemDto> items)
    {
        foreach (var item in items)
        {
            if (!_live.ContainsKey(item.Id))
                Holding = false;
            _live[item.Id] = item;
        }

        Queue = items.ToList();
        if (Confirmation is not null && items.Find(item => item.Id == Confirmation.Id) is { } updated)
            Confirmation = updated;
    }

    void OnHistory(HistoryEntryDto entry)
    {
        _live.Remove(entry.Item.Id);
        _history.Add(entry);
        if (_history.Count > HistoryLimit)
            _history.RemoveRange(0, _history.Count - HistoryLimit);
        Holding = entry.Outcome switch
        {
            SpeechOutcome.Stopped => true,
            SpeechOutcome.Skipped => false,
            _ => Holding,
        };
    }

    static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                    count++;
                inWord = true;
            }
            else
            {
                inWord = false;
            }
        }

        return count;
    }
}
