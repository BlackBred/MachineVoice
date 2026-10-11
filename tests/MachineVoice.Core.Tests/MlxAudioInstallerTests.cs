using MachineVoice.Platform.MacOS;

namespace MachineVoice.Core.Tests;

public class MlxAudioInstallerTests
{
    [Fact]
    public async Task AlreadyInstalled_DoesNotRunUv()
    {
        var lookedForUv = 0;
        var installer = new MlxAudioInstaller(null, () => "/bin/echo", () =>
        {
            lookedForUv++;
            return "/bin/echo";
        });

        Assert.True(installer.IsInstalled);
        await installer.InstallAsync(CancellationToken.None);
        Assert.Equal(0, lookedForUv);
    }

    [Fact]
    public async Task MissingUv_SaysHowToInstallIt()
    {
        var installer = new MlxAudioInstaller(null, () => null, () => null);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => installer.InstallAsync(CancellationToken.None));
        Assert.Contains("uv is not installed", missing.Message);
    }

    [Fact(Timeout = 20000)]
    public async Task Install_RunsUv_AndThenFindsTheServer()
    {
        await using var root = new TempRoot();
        var args = Path.Combine(root.Path, "args");
        var marker = Path.Combine(root.Path, "marker");
        var uv = Script(root.Path, "uv", $$"""
            #!/bin/sh
            printf '%s\n' "$@" > "{{args}}"
            touch "{{marker}}"
            """);

        var installer = new MlxAudioInstaller(null, () => File.Exists(marker) ? marker : null, () => uv);
        Assert.False(installer.IsInstalled);
        await installer.InstallAsync(CancellationToken.None);

        Assert.True(installer.IsInstalled);
        Assert.Equal(
            ["tool", "install", "--python", MlxAudioPackage.Python, MlxAudioPackage.Name],
            await File.ReadAllLinesAsync(args));
    }

    [Fact(Timeout = 20000)]
    public async Task FailedUv_ReportsItsOutput()
    {
        await using var root = new TempRoot();
        var uv = Script(root.Path, "uv", """
            #!/bin/sh
            echo boom >&2
            exit 3
            """);

        var installer = new MlxAudioInstaller(null, () => null, () => uv);
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(CancellationToken.None));
        Assert.Contains("boom", failed.Message);
        Assert.Contains("3", failed.Message);
    }

    [Fact(Timeout = 20000)]
    public async Task UvThatExitsCleanly_StillFails_WhenTheServerDoesNotAppear()
    {
        await using var root = new TempRoot();
        var uv = Script(root.Path, "uv", """
            #!/bin/sh
            exit 0
            """);

        var installer = new MlxAudioInstaller(null, () => null, () => uv);
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(CancellationToken.None));
        Assert.Contains("was not found", failed.Message);
    }

    static string Script(string directory, string name, string body)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
