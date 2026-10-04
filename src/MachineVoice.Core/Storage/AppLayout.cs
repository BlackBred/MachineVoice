namespace MachineVoice.Core;

static class AppLayout
{
    public const string IngestSocketName = "ingest.sock";
    public const string ControlSocketName = "control.sock";
    public const string InboxDirectoryName = "inbox";
    public const string RejectedDirectoryName = "rejected";
    public const string HistoryFileName = "history.json";
    public const string SettingsFileName = "settings.json";

    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void SetPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, PrivateFile);
    }

    public static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, PrivateDirectory);
    }
}
