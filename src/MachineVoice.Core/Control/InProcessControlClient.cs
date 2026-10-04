using System.Runtime.CompilerServices;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class InProcessControlClient : ControlClient
{
    readonly SpeechEngine _engine;
    readonly ClientSlot _slot;
    int _disposed;

    public InProcessControlClient(SpeechEngine engine, ClientSlot slot)
    {
        _engine = engine;
        _slot = slot;
    }

    public override async IAsyncEnumerable<EventMessage> EventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _slot.Outbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_slot.Outbound.Reader.TryRead(out var message))
            {
                if (message is EventMessage ev)
                    yield return ev;
            }
        }
    }

    protected override Task<ResultMessage> SendAsync(ClientMessage message, CancellationToken cancellationToken) =>
        _engine.ExecuteAsync(message, cancellationToken);

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _engine.DetachAsync(_slot).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _slot.Outbound.Writer.TryComplete();
    }
}
