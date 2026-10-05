using MachineVoice.Protocol;

namespace MachineVoice.Core;

public sealed class MachineVoiceOptions
{
    public required string RootDirectory { get; init; }
    public required ITtsEngine Tts { get; init; }
    public Action<string>? Log { get; init; }

    /// <summary>Directory that contains hooks.json. Defaults to ~/.cursor.</summary>
    public string? CursorDirectory { get; init; }

    /// <summary>Handler for the LLM summary requests. Defaults to a regular HTTP handler.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>
    /// The self-contained MachineVoice.Mcp binary inside the app. Connecting MCP to Cursor copies it into
    /// <see cref="RootDirectory"/>. Null in builds without it: the MCP status then reports it as unavailable.
    /// </summary>
    public string? McpServerBinary { get; init; }

    /// <summary>The OmniVoice voices; without it the voice commands fail with invalid-state.</summary>
    public IVoiceStudio? Voices { get; init; }
}

public sealed class MachineVoiceHost : IAsyncDisposable
{
    readonly SpeechEngine _engine;
    readonly IngestServer _ingest;
    readonly ControlServer _control;
    int _disposed;

    MachineVoiceHost(SpeechEngine engine, IngestServer ingest, ControlServer control, string root)
    {
        _engine = engine;
        _ingest = ingest;
        _control = control;
        RootDirectory = root;
        IngestSocketPath = Path.Combine(root, AppLayout.IngestSocketName);
        ControlSocketPath = Path.Combine(root, AppLayout.ControlSocketName);
    }

    public static string DefaultRootDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "MachineVoice");

    public static string ControlSocketIn(string rootDirectory) => Path.Combine(rootDirectory, AppLayout.ControlSocketName);

    public string RootDirectory { get; }
    public string IngestSocketPath { get; }
    public string ControlSocketPath { get; }

    public static async Task<MachineVoiceHost> StartAsync(MachineVoiceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        ArgumentNullException.ThrowIfNull(options.Tts);

        PrepareDirectories(options.RootDirectory);
        var cursorDirectory = options.CursorDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cursor");
        var engine = new SpeechEngine(
            options.RootDirectory,
            cursorDirectory,
            options.Tts,
            options.HttpHandler,
            options.McpServerBinary,
            options.Log,
            options.Voices);
        IngestServer? ingest = null;
        ControlServer? control = null;
        try
        {
            engine.Start();
            await engine.ReplayAsync(cancellationToken).ConfigureAwait(false);
            ingest = new IngestServer(engine, Path.Combine(options.RootDirectory, AppLayout.IngestSocketName), options.Log);
            control = new ControlServer(engine, Path.Combine(options.RootDirectory, AppLayout.ControlSocketName), options.Log);
            ingest.Start();
            control.Start();
            return new MachineVoiceHost(engine, ingest, control, options.RootDirectory);
        }
        catch
        {
            if (control is not null)
                await control.DisposeAsync().ConfigureAwait(false);
            if (ingest is not null)
                await ingest.DisposeAsync().ConfigureAwait(false);
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IControlClient> ConnectInProcessAsync(CancellationToken cancellationToken = default)
    {
        var slot = new ClientSlot();
        await _engine.AttachAsync(slot, cancellationToken).ConfigureAwait(false);
        return new InProcessControlClient(_engine, slot);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _control.DisposeAsync().ConfigureAwait(false);
        await _ingest.DisposeAsync().ConfigureAwait(false);
        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    static void PrepareDirectories(string root)
    {
        var inbox = Path.Combine(root, AppLayout.InboxDirectoryName);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(inbox);
        AppLayout.SetPrivateDirectory(root);
        AppLayout.SetPrivateDirectory(inbox);
    }
}
