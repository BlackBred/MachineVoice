namespace MachineVoice.Protocol;

/// <summary>
/// Control-plane client. The in-process host and the Unix-socket client implement the same contract.
/// </summary>
public interface IControlClient : IAsyncDisposable
{
    IAsyncEnumerable<EventMessage> EventsAsync(CancellationToken cancellationToken = default);

    Task<ResultMessage> PauseAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> ResumeAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> SkipAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> SetModeAsync(PlaybackMode mode, CancellationToken cancellationToken = default);
    Task<ResultMessage> SetPlaybackRateAsync(double rate, CancellationToken cancellationToken = default);

    /// <summary>Seconds of audio from the start of the response being read.</summary>
    Task<ResultMessage> SeekAsync(double position, CancellationToken cancellationToken = default);
    Task<ResultMessage> ListenAsync(string itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts <paramref name="text"/> into the inbox as a clipboard reading. Does not read or write the pasteboard.
    /// </summary>
    Task<ResultMessage> SpeakClipboardAsync(string text, CancellationToken cancellationToken = default);
    Task<ResultMessage> DismissAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> OpenChatAsync(string itemId, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSettingsAsync(CancellationToken cancellationToken = default);
    /// <summary>Changes only the parts that are not null.</summary>
    Task<ResultMessage> UpdateSettingsAsync(
        PlaybackMode? mode = null,
        SummarySettingsDto? summary = null,
        TtsSettingsDto? tts = null,
        QueueOrder? order = null,
        HeadingMode? heading = null,
        HotkeySettingsDto? hotkeys = null,
        CancellationToken cancellationToken = default);
    Task<ResultMessage> ConnectSourceAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> DisconnectSourceAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSourceStatusAsync(string source, CancellationToken cancellationToken = default);
    Task<ResultMessage> GetSnapshotAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> ConnectMcpAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> DisconnectMcpAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> GetMcpStatusAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> ListVoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes from seconds to minutes (the first one downloads the model).</summary>
    Task<ResultMessage> CreateVoiceAsync(CancellationToken cancellationToken = default);
    Task<ResultMessage> SaveVoiceAsync(string draftId, string name, CancellationToken cancellationToken = default);
    Task<ResultMessage> DeleteVoiceAsync(string voiceId, CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="endpoint"/> lists <paramref name="model"/>. Does not change settings.</summary>
    Task<ResultMessage> ProbeSummaryAsync(string endpoint, string model, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>Whether the local mlx-audio server is installed.</summary>
    Task<ResultMessage> GetOmniVoiceStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Installs the server if needed and loads the OmniVoice model. The first run takes minutes.</summary>
    Task<ResultMessage> InstallOmniVoiceAsync(CancellationToken cancellationToken = default);
}
