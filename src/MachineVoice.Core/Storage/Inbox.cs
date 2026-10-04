using System.Text;
using System.Text.Json;

namespace MachineVoice.Core;

sealed class Inbox
{
    readonly string _directory;
    readonly string _rejected;
    int _counter;

    public Inbox(string root)
    {
        _directory = Path.Combine(root, AppLayout.InboxDirectoryName);
        _rejected = Path.Combine(root, AppLayout.RejectedDirectoryName);
    }

    public string NewPath()
    {
        var n = Interlocked.Increment(ref _counter);
        var name = $"{DateTime.UtcNow.Ticks:D20}-{n:D6}-{Guid.NewGuid():N}.json";
        return Path.Combine(_directory, name);
    }

    public void Write(string path, StoredSubmit submit)
    {
        var json = JsonSerializer.Serialize(submit, IngestJsonContext.Default.StoredSubmit);
        DurableFile.Write(path, Encoding.UTF8.GetBytes(json));
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(_directory))
            return [];

        return Directory.GetFiles(_directory, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public StoredSubmit Read(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, IngestJsonContext.Default.StoredSubmit)
            ?? throw new InvalidDataException("Empty inbox file.");
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public void Quarantine(string path)
    {
        Directory.CreateDirectory(_rejected);
        AppLayout.SetPrivateDirectory(_rejected);
        var dest = Path.Combine(_rejected, Path.GetFileName(path));
        File.Move(path, dest, overwrite: true);
    }

    public void DeleteTemps()
    {
        if (!Directory.Exists(_directory))
            return;

        foreach (var temp in Directory.GetFiles(_directory, "*.tmp"))
            File.Delete(temp);
    }
}
