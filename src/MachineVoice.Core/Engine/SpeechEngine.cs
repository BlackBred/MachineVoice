using System.Threading.Channels;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class SpeechEngine : IAsyncDisposable
{
    readonly Inbox _inbox;
    readonly HistoryStore _history;
    readonly SettingsStore _settingsStore;
    readonly ITtsEngine _tts;
    readonly IQueuePolicy _policy;
    readonly Action<string>? _log;
    readonly Channel<Work> _work = Channel.CreateUnbounded<Work>();
    readonly CancellationTokenSource _shutdown = new();
    readonly List<SpeechItem> _queue = [];
    readonly List<ClientSlot> _clients = [];
    readonly Dictionary<string, string> _idByGeneration = new(StringComparer.Ordinal);
    readonly Dictionary<string, SpeechItemDto> _byId = new(StringComparer.Ordinal);

    SettingsDto _settings = new();
    PlaybackMode _mode = PlaybackMode.Auto;
    PlayerState _player = PlayerState.Idle;
    SpeechItem? _current;
    string? _confirmationItemId;
    bool _hold;
    Task? _loop;
    int _disposed;

    public SpeechEngine(string root, ITtsEngine tts, IQueuePolicy policy, Action<string>? log)
    {
        _inbox = new Inbox(root);
        _history = new HistoryStore(Path.Combine(root, AppLayout.HistoryFileName), log);
        _settingsStore = new SettingsStore(Path.Combine(root, AppLayout.SettingsFileName), log);
        _tts = tts;
        _policy = policy;
        _log = log;
        _tts.Progress += (_, args) => Post(new ProgressWork(args));
        _tts.Completed += (_, args) => Post(new CompletedWork(args.UtteranceId));
    }

    public void Start()
    {
        _history.Load();
        foreach (var entry in _history.Entries)
            RememberHistory(entry.Item);

        _settings = _settingsStore.Load();
        _mode = _settings.Mode;
        _inbox.DeleteTemps();
        _loop = Task.Run(RunAsync);
    }

    public Task ReplayAsync(CancellationToken cancellationToken = default) =>
        SignalAsync(() =>
        {
            foreach (var path in _inbox.List())
                TryReplay(path);
            Advance();
        }, cancellationToken);

    public Task<IngestResponse> SubmitAsync(StoredSubmit submit, CancellationToken cancellationToken = default)
    {
        var ack = new TaskCompletionSource<IngestResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new SubmitWork(submit, ack)))
            return Task.FromException<IngestResponse>(new ObjectDisposedException(nameof(SpeechEngine)));
        return WaitAsync(ack, cancellationToken);
    }

    public Task<ResultMessage> ExecuteAsync(ClientMessage command, CancellationToken cancellationToken = default)
    {
        var result = new TaskCompletionSource<ResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new CommandWork(command, result)))
            return Task.FromException<ResultMessage>(new ObjectDisposedException(nameof(SpeechEngine)));
        return WaitAsync(result, cancellationToken);
    }

    public Task AttachAsync(ClientSlot slot, CancellationToken cancellationToken = default) =>
        SignalAsync(() =>
        {
            _clients.Add(slot);
            slot.Outbound.Writer.TryWrite(new SnapshotEvent { Snapshot = BuildSnapshot() });
        }, cancellationToken);

    public Task DetachAsync(ClientSlot slot, CancellationToken cancellationToken = default) =>
        SignalAsync(() => _clients.Remove(slot), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        _work.Writer.TryComplete();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            _tts.Stop();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"TTS stop failed: {ex.Message}");
        }

        _shutdown.Dispose();
    }

    async Task RunAsync()
    {
        try
        {
            await foreach (var work in _work.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    Dispatch(work);
                }
                catch (Exception ex)
                {
                    _log?.Invoke(ex.ToString());
                    Fail(work, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    void Dispatch(Work work)
    {
        switch (work)
        {
            case SubmitWork submit:
                submit.Ack.TrySetResult(AcceptNew(submit.Submit));
                break;
            case CommandWork command:
                command.Result.TrySetResult(Execute(command.Command));
                break;
            case ProgressWork progress:
                OnProgress(progress.Args);
                break;
            case CompletedWork completed:
                OnCompleted(completed.UtteranceId);
                break;
            case SignalWork signal:
                signal.Action();
                signal.Done.TrySetResult();
                break;
        }
    }

    static void Fail(Work work, Exception ex)
    {
        switch (work)
        {
            case SubmitWork submit:
                submit.Ack.TrySetException(ex);
                break;
            case CommandWork command:
                command.Result.TrySetException(ex);
                break;
            case SignalWork signal:
                signal.Done.TrySetException(ex);
                break;
        }
    }

    IngestResponse AcceptNew(StoredSubmit submit)
    {
        var rejection = SubmitRules.Validate(submit);
        if (rejection is not null)
            return IngestResponse.Rejected(rejection);

        // The file is durable before the caller is told the event was accepted.
        // A crash after the write and before history is rebuilt from inbox on the next start.
        var id = Guid.CreateVersion7().ToString("N");
        var receivedAt = DateTimeOffset.UtcNow;
        var stored = SubmitRules.Canonical(submit, id, receivedAt);
        var path = _inbox.NewPath();
        _inbox.Write(path, stored);

        if (_idByGeneration.TryGetValue(stored.GenerationId!, out var existingId))
        {
            _inbox.Delete(path);
            return IngestResponse.Accepted(existingId, duplicate: true);
        }

        var item = ToItem(stored, path);
        Remember(item);
        _hold = false;
        _policy.Enqueue(_queue, item);
        PublishQueue();
        Advance();
        return IngestResponse.Accepted(item.Id, duplicate: false);
    }

    void TryReplay(string path)
    {
        StoredSubmit stored;
        try
        {
            stored = _inbox.Read(path);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            _log?.Invoke($"Quarantining unreadable inbox file {path}: {ex.Message}");
            _inbox.Quarantine(path);
            return;
        }

        var rejection = SubmitRules.Validate(stored);
        if (rejection is not null)
        {
            _log?.Invoke($"Quarantining invalid inbox file {path}: {rejection}");
            _inbox.Quarantine(path);
            return;
        }

        var generationId = stored.GenerationId!.Trim();
        if (_idByGeneration.ContainsKey(generationId))
        {
            _inbox.Delete(path);
            return;
        }

        var id = string.IsNullOrWhiteSpace(stored.Id) ? Guid.CreateVersion7().ToString("N") : stored.Id!;
        var receivedAt = stored.ReceivedAt ?? File.GetLastWriteTimeUtc(path);
        var canonical = SubmitRules.Canonical(stored, id, receivedAt);
        var item = ToItem(canonical, path);
        Remember(item);
        _policy.Enqueue(_queue, item);
    }

    ResultMessage Execute(ClientMessage command)
    {
        if (string.IsNullOrWhiteSpace(command.Id))
            return Fail(command.Id, ProtocolErrors.InvalidArgument);
        if (command.Version != ProtocolVersion.Current)
            return Fail(command.Id, ProtocolErrors.UnsupportedVersion);

        return command switch
        {
            PauseCommand => Pause(command.Id),
            ResumeCommand => Resume(command.Id),
            StopCommand => Stop(command.Id),
            SkipCommand => Skip(command.Id),
            SetModeCommand setMode => ApplyMode(command.Id, setMode.Mode),
            ListenCommand listen => Listen(command.Id, listen.ItemId),
            DismissCommand dismiss => Dismiss(command.Id, dismiss.ItemId),
            OpenChatCommand open => OpenChat(command.Id, open.ItemId),
            GetSettingsCommand => Ok(command.Id, settings: CopySettings()),
            UpdateSettingsCommand update => ApplyMode(command.Id, update.Mode),
            ConnectSourceCommand connect => SetSource(command.Id, connect.Source, enabled: true),
            DisconnectSourceCommand disconnect => SetSource(command.Id, disconnect.Source, enabled: false),
            GetSourceStatusCommand status => SourceStatus(command.Id, status.Source),
            GetSnapshotCommand => Ok(command.Id, snapshot: BuildSnapshot()),
            _ => Fail(command.Id, ProtocolErrors.UnknownCommand),
        };
    }

    ResultMessage Pause(string id)
    {
        if (_player != PlayerState.Speaking)
            return Fail(id, ProtocolErrors.InvalidState);

        _tts.Pause();
        _player = PlayerState.Paused;
        Publish(new PlayerStateEvent { State = PlayerState.Paused, ItemId = _current?.Id });
        return Ok(id);
    }

    ResultMessage Resume(string id)
    {
        if (_player == PlayerState.Paused)
        {
            _tts.Resume();
            _player = PlayerState.Speaking;
            Publish(new PlayerStateEvent { State = PlayerState.Speaking, ItemId = _current?.Id });
            return Ok(id);
        }

        if (_player == PlayerState.Idle && _hold && _queue.Count > 0)
        {
            _hold = false;
            Advance();
            return Ok(id);
        }

        return Fail(id, ProtocolErrors.InvalidState);
    }

    ResultMessage Stop(string id)
    {
        if (_current is null || _player is not (PlayerState.Speaking or PlayerState.Paused))
            return Fail(id, ProtocolErrors.InvalidState);

        var item = _current;
        _tts.Stop();
        _current = null;
        _player = PlayerState.Idle;
        _hold = true;
        Publish(new PlayerStateEvent { State = PlayerState.Idle });
        Archive(item, SpeechOutcome.Stopped);
        return Ok(id);
    }

    ResultMessage Skip(string id)
    {
        _hold = false;
        if (_current is not null)
        {
            var item = _current;
            _tts.Stop();
            _current = null;
            _player = PlayerState.Idle;
            Publish(new PlayerStateEvent { State = PlayerState.Idle });
            Archive(item, SpeechOutcome.Skipped);
            Advance();
            return Ok(id);
        }

        if (_confirmationItemId is not null)
            return Dismiss(id, _confirmationItemId);

        if (_queue.Count == 0)
            return Fail(id, ProtocolErrors.InvalidState);

        var head = _queue[0];
        _queue.RemoveAt(0);
        PublishQueue();
        Archive(head, SpeechOutcome.Skipped);
        Advance();
        return Ok(id);
    }

    ResultMessage Listen(string id, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return Fail(id, ProtocolErrors.InvalidArgument);
        if (_confirmationItemId != itemId || _player != PlayerState.Idle)
            return Fail(id, _confirmationItemId == itemId ? ProtocolErrors.InvalidState : ProtocolErrors.NotFound);

        var item = TakeQueued(itemId);
        if (item is null)
            return Fail(id, ProtocolErrors.NotFound);

        ClearConfirmation();
        PublishQueue();
        BeginSpeak(item);
        return Ok(id);
    }

    ResultMessage Dismiss(string id, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return Fail(id, ProtocolErrors.InvalidArgument);
        if (_confirmationItemId != itemId)
            return Fail(id, ProtocolErrors.NotFound);

        var item = TakeQueued(itemId);
        if (item is null)
            return Fail(id, ProtocolErrors.NotFound);

        ClearConfirmation();
        PublishQueue();
        Archive(item, SpeechOutcome.Skipped);
        Advance();
        return Ok(id);
    }

    ResultMessage OpenChat(string id, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return Fail(id, ProtocolErrors.InvalidArgument);
        if (!_byId.TryGetValue(itemId, out var item))
            return Fail(id, ProtocolErrors.NotFound);

        Publish(new ChatRequestedEvent
        {
            ItemId = item.Id,
            Source = item.Source,
            ConversationId = item.ConversationId,
            Project = item.Project,
        });
        return Ok(id);
    }

    ResultMessage ApplyMode(string id, PlaybackMode? mode)
    {
        if (mode is null)
            return Fail(id, ProtocolErrors.InvalidArgument);
        if (_mode == mode)
            return Ok(id);

        _mode = mode.Value;
        _hold = false;
        if (_confirmationItemId is not null && _mode != PlaybackMode.Confirm)
            ClearConfirmation();

        PersistSettings();
        Publish(new SettingsChangedEvent { Settings = CopySettings() });
        Advance();
        return Ok(id);
    }

    ResultMessage SetSource(string id, string source, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Trim().Length > SubmitRules.MaxSource)
            return Fail(id, ProtocolErrors.InvalidArgument);

        source = source.Trim();
        if (!enabled && _settings.Sources.All(s => !string.Equals(s.Name, source, StringComparison.Ordinal)))
            return Ok(id, source: new SourceStatusDto { Name = source, Status = SourceConnectionStatus.Disconnected });

        var sources = _settings.Sources
            .Where(s => !string.Equals(s.Name, source, StringComparison.Ordinal))
            .Select(s => new SourceSettingDto { Name = s.Name, Enabled = s.Enabled })
            .ToList();
        sources.Add(new SourceSettingDto { Name = source, Enabled = enabled });
        sources.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));
        _settings = new SettingsDto { Mode = _mode, Sources = sources };
        _settingsStore.Save(_settings);

        var status = enabled ? SourceConnectionStatus.Connected : SourceConnectionStatus.Disconnected;
        Publish(new SettingsChangedEvent { Settings = CopySettings() });
        Publish(new SourceChangedEvent { Source = source, Status = status });
        return Ok(id, source: new SourceStatusDto { Name = source, Status = status });
    }

    ResultMessage SourceStatus(string id, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Fail(id, ProtocolErrors.InvalidArgument);

        source = source.Trim();
        return Ok(id, source: new SourceStatusDto { Name = source, Status = StatusOf(source) });
    }

    void OnProgress(TtsProgressEventArgs args)
    {
        if (_player != PlayerState.Speaking || _current?.Id != args.UtteranceId)
            return;

        Publish(new PlayerProgressEvent
        {
            ItemId = args.UtteranceId,
            WordIndex = args.WordIndex,
            Word = args.Word,
        });
    }

    void OnCompleted(string utteranceId)
    {
        if (_current is null || _current.Id != utteranceId)
            return;

        var item = _current;
        _current = null;
        _player = PlayerState.Idle;
        Publish(new PlayerStateEvent { State = PlayerState.Idle });
        Archive(item, SpeechOutcome.Spoken);
        Advance();
    }

    void Advance()
    {
        if (_player != PlayerState.Idle || _hold || _confirmationItemId is not null || _queue.Count == 0)
            return;
        if (_mode == PlaybackMode.Silent)
            return;

        if (_mode == PlaybackMode.Confirm)
        {
            var head = _queue[0];
            _confirmationItemId = head.Id;
            Publish(new ConfirmationRequestedEvent { Item = ToDto(head) });
            return;
        }

        var next = _queue[0];
        _queue.RemoveAt(0);
        PublishQueue();
        BeginSpeak(next);
    }

    void BeginSpeak(SpeechItem item)
    {
        _current = item;
        try
        {
            _tts.Speak(item.Id, item.Text);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"TTS speak failed: {ex.Message}");
            _current = null;
            _player = PlayerState.Idle;
            _hold = true;
            Publish(new PlayerStateEvent { State = PlayerState.Idle });
            Archive(item, SpeechOutcome.Stopped);
            return;
        }

        _player = PlayerState.Speaking;
        Publish(new PlayerStateEvent { State = PlayerState.Speaking, ItemId = item.Id });
    }

    void Archive(SpeechItem item, SpeechOutcome outcome)
    {
        var entry = new HistoryEntryDto
        {
            Item = ToDto(item),
            Outcome = outcome,
            FinishedAt = DateTimeOffset.UtcNow,
        };
        _history.Append(entry);
        _inbox.Delete(item.InboxPath);
        Publish(new HistoryAppendedEvent { Entry = entry });
    }

    void ClearConfirmation()
    {
        if (_confirmationItemId is null)
            return;

        var itemId = _confirmationItemId;
        _confirmationItemId = null;
        Publish(new ConfirmationClearedEvent { ItemId = itemId });
    }

    SpeechItem? TakeQueued(string itemId)
    {
        var index = _queue.FindIndex(item => item.Id == itemId);
        if (index < 0)
            return null;

        var item = _queue[index];
        _queue.RemoveAt(index);
        return item;
    }

    void PersistSettings()
    {
        _settings = new SettingsDto
        {
            Mode = _mode,
            Sources = _settings.Sources.Select(s => new SourceSettingDto { Name = s.Name, Enabled = s.Enabled }).ToList(),
        };
        _settingsStore.Save(_settings);
    }

    void Remember(SpeechItem item)
    {
        _byId[item.Id] = ToDto(item);
        _idByGeneration[item.GenerationId] = item.Id;
    }

    void RememberHistory(SpeechItemDto item)
    {
        _byId[item.Id] = item;
        if (!string.IsNullOrEmpty(item.GenerationId))
            _idByGeneration[item.GenerationId] = item.Id;
    }

    SnapshotDto BuildSnapshot() => new()
    {
        Player = _player,
        CurrentItemId = _current?.Id,
        Current = _current is null ? null : ToDto(_current),
        Mode = _mode,
        Holding = _hold,
        Confirmation = ConfirmationDto(),
        Queue = _queue.Select(ToDto).ToList(),
        History = _history.Entries.ToList(),
        Settings = CopySettings(),
        Sources = SourceStatuses(),
    };

    SpeechItemDto? ConfirmationDto()
    {
        if (_confirmationItemId is null)
            return null;
        var item = _queue.FirstOrDefault(candidate => candidate.Id == _confirmationItemId);
        return item is null ? null : ToDto(item);
    }

    SettingsDto CopySettings() => new()
    {
        Mode = _settings.Mode,
        Sources = _settings.Sources.Select(s => new SourceSettingDto { Name = s.Name, Enabled = s.Enabled }).ToList(),
    };

    List<SourceStatusDto> SourceStatuses() =>
        _settings.Sources
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new SourceStatusDto { Name = s.Name, Status = s.Enabled ? SourceConnectionStatus.Connected : SourceConnectionStatus.Disconnected })
            .ToList();

    SourceConnectionStatus StatusOf(string source)
    {
        var entry = _settings.Sources.FirstOrDefault(s => string.Equals(s.Name, source, StringComparison.Ordinal));
        return entry is { Enabled: true } ? SourceConnectionStatus.Connected : SourceConnectionStatus.Disconnected;
    }

    void PublishQueue() => Publish(new QueueChangedEvent { Items = _queue.Select(ToDto).ToList() });

    void Publish(EventMessage message)
    {
        foreach (var client in _clients)
            client.Outbound.Writer.TryWrite(message);
    }

    static SpeechItemDto ToDto(SpeechItem item) => new()
    {
        Id = item.Id,
        Source = item.Source,
        Project = item.Project,
        ConversationId = item.ConversationId,
        GenerationId = item.GenerationId,
        Topic = item.Topic,
        Text = item.Text,
        ReceivedAt = item.ReceivedAt,
    };

    static SpeechItem ToItem(StoredSubmit stored, string path) => new()
    {
        Id = stored.Id!,
        Source = stored.Source!,
        Project = stored.Project,
        ConversationId = stored.ConversationId,
        GenerationId = stored.GenerationId!,
        Topic = stored.Topic,
        Text = stored.Text!,
        ReceivedAt = stored.ReceivedAt ?? DateTimeOffset.UtcNow,
        InboxPath = path,
    };

    bool Post(Work work) => Volatile.Read(ref _disposed) == 0 && _work.Writer.TryWrite(work);

    Task SignalAsync(Action action, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Post(new SignalWork(action, done)))
            return Task.FromException(new ObjectDisposedException(nameof(SpeechEngine)));
        return WaitAsync(done, cancellationToken);
    }

    async Task<T> WaitAsync<T>(TaskCompletionSource<T> done, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        return await done.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    async Task WaitAsync(TaskCompletionSource done, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await done.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    static ResultMessage Ok(string id, SettingsDto? settings = null, SourceStatusDto? source = null, SnapshotDto? snapshot = null) =>
        new()
        {
            Id = id,
            Ok = true,
            Settings = settings,
            Source = source,
            Snapshot = snapshot,
        };

    static ResultMessage Fail(string id, string error) => new()
    {
        Id = id,
        Ok = false,
        Error = error,
    };

    abstract class Work;

    sealed class SubmitWork(StoredSubmit submit, TaskCompletionSource<IngestResponse> ack) : Work
    {
        public StoredSubmit Submit { get; } = submit;
        public TaskCompletionSource<IngestResponse> Ack { get; } = ack;
    }

    sealed class CommandWork(ClientMessage command, TaskCompletionSource<ResultMessage> result) : Work
    {
        public ClientMessage Command { get; } = command;
        public TaskCompletionSource<ResultMessage> Result { get; } = result;
    }

    sealed class ProgressWork(TtsProgressEventArgs args) : Work
    {
        public TtsProgressEventArgs Args { get; } = args;
    }

    sealed class CompletedWork(string utteranceId) : Work
    {
        public string UtteranceId { get; } = utteranceId;
    }

    sealed class SignalWork(Action action, TaskCompletionSource done) : Work
    {
        public Action Action { get; } = action;
        public TaskCompletionSource Done { get; } = done;
    }
}
