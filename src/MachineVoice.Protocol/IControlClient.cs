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
    Task<ResultMessage> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default);

    /// <summary>Seconds of audio from the start of the response being read.</summary>
    Task<ResultMessage> SeekAsync(double position, CancellationToken cancellationToken = default);
    Task<ResultMessage> ListenAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> DismissAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> OpenChatAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>Changes only the parts that are not null.</summary>
    Task<ResultMessage> UpdateSettingsAsync(
        PlaybackMode? mode = null,
        SummarySettingsDto? summary = null,
        TtsSettingsDto? tts = null,
        QueueOrder? order = null,
        CancellationToken cancellationToken = default);
    Task<ResultMessage> ConnectSourceAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> DisconnectSourceAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSourceStatusAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> ConnectMcpAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> DisconnectMcpAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> GetMcpStatusAsync(CancellationToken cancellationToken = default);
}
