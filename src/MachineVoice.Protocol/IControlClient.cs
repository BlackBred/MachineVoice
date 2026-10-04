namespace MachineVoice.Protocol;

/// <summary>
/// Control-plane client. The in-process host and the Unix-socket client implement the same contract.
/// </summary>
public interface IControlClient : IAsyncDisposable
{
    IAsyncEnumerable<EventMessage> EventsAsync(CancellationToken cancellationToken = default);

    Task<ResultMessage> PauseAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> ResumeAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> StopAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> SkipAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> SetModeAsync(PlaybackMode mode, CancellationToken cancellationToken = default);
    Task<ResultMessage> ListenAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> DismissAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> OpenChatAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> UpdateSettingsAsync(PlaybackMode mode, CancellationToken cancellationToken = default);
    Task<ResultMessage> ConnectSourceAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> DisconnectSourceAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSourceStatusAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
