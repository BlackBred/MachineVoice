using MachineVoice.Protocol;

namespace MachineVoice.Core.Tests;

sealed class EventLog
{
    readonly List<EventMessage> _items = [];
    Exception? _error;

    public void Add(EventMessage message)
    {
        lock (_items)
            _items.Add(message);
    }

    public void Fault(Exception exception)
    {
        lock (_items)
            _error = exception;
    }

    public async Task<T> TakeAsync<T>(Func<T, bool>? match = null, int timeoutMs = 5000) where T : EventMessage
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            lock (_items)
            {
                if (_error is not null && _items.Count == 0)
                    throw new InvalidOperationException("Event stream failed.", _error);

                for (var i = 0; i < _items.Count; i++)
                {
                    if (_items[i] is T typed && (match is null || match(typed)))
                    {
                        _items.RemoveAt(i);
                        return typed;
                    }
                }
            }

            if (Environment.TickCount64 > deadline)
            {
                string pending;
                lock (_items)
                    pending = _items.Count == 0 ? "(empty)" : string.Join(", ", _items.Select(Describe));
                throw new TimeoutException($"Timed out waiting for {typeof(T).Name}. Pending: {pending}");
            }

            await Task.Delay(15);
        }
    }

    public static EventLog Pump(IControlClient client)
    {
        var log = new EventLog();
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in client.EventsAsync())
                    log.Add(ev);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Fault(ex);
            }
        });
        return log;
    }

    static string Describe(EventMessage message) => message switch
    {
        PlayerStateEvent state => $"player.state:{state.State}:{state.ItemId}",
        PlayerProgressEvent progress => $"player.progress:{progress.Word}",
        QueueChangedEvent queue => $"queue:{string.Join(",", queue.Items.Select(item => item.GenerationId))}",
        HistoryAppendedEvent history => $"history:{history.Entry.Outcome}:{history.Entry.Item.GenerationId}",
        ConfirmationRequestedEvent confirmation => $"confirmation:{confirmation.Item.GenerationId}",
        ConfirmationClearedEvent cleared => $"cleared:{cleared.ItemId}",
        SettingsChangedEvent settings => $"settings:{settings.Settings.Mode}",
        SourceChangedEvent source => $"source:{source.Source}:{source.Status}",
        ChatRequestedEvent chat => $"chat:{chat.ItemId}",
        SnapshotEvent => "snapshot",
        _ => message.GetType().Name,
    };
}
