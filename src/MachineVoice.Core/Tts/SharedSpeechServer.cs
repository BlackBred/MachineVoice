namespace MachineVoice.Core;

/// <summary>
/// One speech server for several synthesizers, each through a lease of its own. The server is busy while any
/// lease is; once all are idle it may stop after the longest of their delays. A disposed lease no longer counts.
/// </summary>
public sealed class SharedSpeechServer(ISpeechServer server) : IDisposable
{
    readonly ISpeechServer _server = server;
    readonly object _gate = new();
    readonly List<Holder> _leases = [];
    bool _disposed;

    public ISpeechServer Lease()
    {
        var lease = new Holder(this);
        lock (_gate)
            _leases.Add(lease);
        return lease;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _server.Dispose();
    }

    // The inner server is called under _gate, so its calls keep the order of the decisions.
    void Update()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var used = _leases.Where(lease => lease.State != LeaseState.Unused).ToList();
            if (used.Count == 0)
                return;
            if (used.Any(lease => lease.State == LeaseState.Busy))
            {
                _server.Busy();
                return;
            }

            _server.Idle(used.Any(lease => lease.UnloadAfter is null) ? null : used.Max(lease => lease.UnloadAfter));
        }
    }

    void Release(Holder lease)
    {
        lock (_gate)
            _leases.Remove(lease);
        Update();
    }

    enum LeaseState
    {
        Unused,
        Busy,
        Idle,
    }

    sealed class Holder(SharedSpeechServer owner) : ISpeechServer
    {
        // Guarded by the owner's _gate.
        public LeaseState State { get; private set; }
        public TimeSpan? UnloadAfter { get; private set; }

        public Task EnsureRunningAsync(Uri endpoint, CancellationToken cancellationToken) =>
            owner._server.EnsureRunningAsync(endpoint, cancellationToken);

        public void Busy()
        {
            lock (owner._gate)
                State = LeaseState.Busy;
            owner.Update();
        }

        public void Idle(TimeSpan? unloadAfter)
        {
            lock (owner._gate)
            {
                State = LeaseState.Idle;
                UnloadAfter = unloadAfter;
            }

            owner.Update();
        }

        public void Dispose() => owner.Release(this);
    }
}
