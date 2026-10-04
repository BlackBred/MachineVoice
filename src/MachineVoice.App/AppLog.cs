namespace MachineVoice.App;

/// <summary>stderr plus app.log in the data directory. The file starts over once it grows past 1 MB.</summary>
sealed class AppLog
{
    const long MaxBytes = 1024 * 1024;

    readonly object _gate = new();
    readonly string _path;

    public AppLog(string directory)
    {
        _path = Path.Combine(directory, "app.log");
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        lock (_gate)
        {
            Console.Error.WriteLine(line);
            try
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }
}
