namespace MachineVoice.Core;

/// <summary>
/// Installs mlx_audio.server, which serves OmniVoice (and Qwen3-TTS). The model weights are a separate download
/// that happens when the model is loaded.
/// </summary>
public interface IMlxAudioInstaller
{
    /// <summary>The server command is on disk.</summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Installs the server when it is missing. Does nothing when <see cref="IsInstalled"/> is already true.
    /// Throws when uv is missing or the install fails.
    /// </summary>
    Task InstallAsync(CancellationToken cancellationToken);
}
