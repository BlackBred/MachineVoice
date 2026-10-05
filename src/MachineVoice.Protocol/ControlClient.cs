namespace MachineVoice.Protocol;

public abstract class ControlClient : IControlClient
{
    public Task<ResultMessage> PauseAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new PauseCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> ResumeAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new ResumeCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> StopAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new StopCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> SkipAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new SkipCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> SetModeAsync(PlaybackMode mode, CancellationToken cancellationToken = default) =>
        SendAsync(new SetModeCommand { Version = ProtocolVersion.Current, Id = NewId(), Mode = mode }, cancellationToken);

    public Task<ResultMessage> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default) =>
        SendAsync(new SetPlaybackRateCommand { Version = ProtocolVersion.Current, Id = NewId(), Rate = rate }, cancellationToken);

    public Task<ResultMessage> ListenAsync(string itemId, CancellationToken cancellationToken = default) =>
        SendAsync(new ListenCommand { Version = ProtocolVersion.Current, Id = NewId(), ItemId = itemId }, cancellationToken);

    public Task<ResultMessage> DismissAsync(string itemId, CancellationToken cancellationToken = default) =>
        SendAsync(new DismissCommand { Version = ProtocolVersion.Current, Id = NewId(), ItemId = itemId }, cancellationToken);

    public Task<ResultMessage> OpenChatAsync(string itemId, CancellationToken cancellationToken = default) =>
        SendAsync(new OpenChatCommand { Version = ProtocolVersion.Current, Id = NewId(), ItemId = itemId }, cancellationToken);

    public Task<ResultMessage> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new GetSettingsCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> UpdateSettingsAsync(
        PlaybackMode? mode = null,
        SummarySettingsDto? summary = null,
        TtsSettingsDto? tts = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(new UpdateSettingsCommand { Version = ProtocolVersion.Current, Id = NewId(), Mode = mode, Summary = summary, Tts = tts }, cancellationToken);

    public Task<ResultMessage> ConnectSourceAsync(string source, CancellationToken cancellationToken = default) =>
        SendAsync(new ConnectSourceCommand { Version = ProtocolVersion.Current, Id = NewId(), Source = source }, cancellationToken);

    public Task<ResultMessage> DisconnectSourceAsync(string source, CancellationToken cancellationToken = default) =>
        SendAsync(new DisconnectSourceCommand { Version = ProtocolVersion.Current, Id = NewId(), Source = source }, cancellationToken);

    public Task<ResultMessage> GetSourceStatusAsync(string source, CancellationToken cancellationToken = default) =>
        SendAsync(new GetSourceStatusCommand { Version = ProtocolVersion.Current, Id = NewId(), Source = source }, cancellationToken);

    public Task<ResultMessage> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new GetSnapshotCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> ConnectMcpAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new ConnectMcpCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> DisconnectMcpAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new DisconnectMcpCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public Task<ResultMessage> GetMcpStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync(new GetMcpStatusCommand { Version = ProtocolVersion.Current, Id = NewId() }, cancellationToken);

    public abstract IAsyncEnumerable<EventMessage> EventsAsync(CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();

    protected abstract Task<ResultMessage> SendAsync(ClientMessage message, CancellationToken cancellationToken);

    protected static string NewId() => Guid.NewGuid().ToString("N");
}
