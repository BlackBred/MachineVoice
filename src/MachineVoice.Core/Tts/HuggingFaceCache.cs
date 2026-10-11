namespace MachineVoice.Core;

/// <summary>
/// The local Hugging Face hub cache. mlx-audio downloads OmniVoice there and reuses the files, so an install does
/// not fetch a model that is already on disk.
/// </summary>
public static class HuggingFaceCache
{
    /// <summary>The cache mlx-audio uses: <c>HF_HUB_CACHE</c>, otherwise <c>HF_HOME/hub</c>, otherwise the default.</summary>
    public static string Root()
    {
        var hub = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        if (!string.IsNullOrWhiteSpace(hub))
            return hub;
        var legacy = Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_CACHE");
        if (!string.IsNullOrWhiteSpace(legacy))
            return legacy;

        var home = Environment.GetEnvironmentVariable("HF_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            home = Path.Combine(
                string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache") : xdg,
                "huggingface");
        }

        return Path.Combine(home, "hub");
    }

    /// <summary>The snapshot for <paramref name="modelId"/> has its weights. A half-finished download does not.</summary>
    public static bool IsDownloaded(string modelId) => IsDownloaded(Root(), modelId);

    public static bool IsDownloaded(string cacheRoot, string modelId)
    {
        try
        {
            if (!RepoFolder(modelId, out var folder))
                return false;
            var revisionPath = Path.Combine(cacheRoot, folder, "refs", "main");
            if (!File.Exists(revisionPath))
                return false;
            var revision = File.ReadAllText(revisionPath).Trim();
            if (revision.Length is 0 or > 128 || revision.IndexOfAny(['/', '\\']) >= 0 || revision.Contains("..", StringComparison.Ordinal))
                return false;

            var snapshot = Path.Combine(cacheRoot, folder, "snapshots", revision);
            return Directory.Exists(snapshot)
                && Directory.EnumerateFiles(snapshot, "*.safetensors").Any(WeightFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A finished download is a symlink to a blob. <see cref="File.Exists"/> is true for a broken link too, so the
    /// target has to open and hold the weights.
    /// </summary>
    static bool WeightFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is { } target)
        {
            var directory = info.DirectoryName ?? ".";
            var full = Path.IsPathRooted(target) ? target : Path.GetFullPath(target, directory);
            info = new FileInfo(full);
        }

        try
        {
            return info.Exists && info.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><c>org/name</c> becomes <c>models--org--name</c>, the folder name Hugging Face uses.</summary>
    static bool RepoFolder(string modelId, out string folder)
    {
        folder = "";
        var parts = modelId.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".." || part.Contains('\\', StringComparison.Ordinal)))
            return false;
        folder = "models--" + string.Join("--", parts);
        return true;
    }
}
