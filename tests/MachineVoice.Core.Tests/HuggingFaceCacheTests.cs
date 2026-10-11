using MachineVoice.Core;

namespace MachineVoice.Core.Tests;

public class HuggingFaceCacheTests
{
    [Fact]
    public async Task DownloadedWeights_Count_AMissingSnapshotDoesNot()
    {
        await using var root = new TempRoot();
        var revision = "abc123";
        var snapshot = Path.Combine(root.Path, "models--org--OmniVoice", "snapshots", revision);
        Directory.CreateDirectory(snapshot);
        await File.WriteAllTextAsync(Path.Combine(snapshot, "model.safetensors"), "weights");
        var refs = Path.Combine(root.Path, "models--org--OmniVoice", "refs");
        Directory.CreateDirectory(refs);
        await File.WriteAllTextAsync(Path.Combine(refs, "main"), revision + "\n");

        Assert.True(HuggingFaceCache.IsDownloaded(root.Path, "org/OmniVoice"));
        Assert.False(HuggingFaceCache.IsDownloaded(root.Path, "org/Other"));
        Assert.False(HuggingFaceCache.IsDownloaded(root.Path, "../org/OmniVoice"));

        File.Delete(Path.Combine(snapshot, "model.safetensors"));
        Assert.False(HuggingFaceCache.IsDownloaded(root.Path, "org/OmniVoice"));
    }

    [Fact]
    public async Task BrokenWeightLink_IsNotDownloaded()
    {
        if (OperatingSystem.IsWindows())
            return;

        await using var root = new TempRoot();
        var revision = "abc123";
        var snapshot = Path.Combine(root.Path, "models--org--OmniVoice", "snapshots", revision);
        Directory.CreateDirectory(snapshot);
        File.CreateSymbolicLink(Path.Combine(snapshot, "model.safetensors"), "missing-blob");
        var refs = Path.Combine(root.Path, "models--org--OmniVoice", "refs");
        Directory.CreateDirectory(refs);
        await File.WriteAllTextAsync(Path.Combine(refs, "main"), revision);

        Assert.False(HuggingFaceCache.IsDownloaded(root.Path, "org/OmniVoice"));
    }
}
