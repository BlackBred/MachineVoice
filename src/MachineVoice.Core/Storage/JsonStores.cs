using System.Text;
using System.Text.Json;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

sealed class HistoryStore
{
    readonly string _path;
    readonly Action<string>? _log;

    public HistoryStore(string path, Action<string>? log)
    {
        _path = path;
        _log = log;
    }

    public List<HistoryEntryDto> Entries { get; private set; } = [];

    public void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            var json = File.ReadAllText(_path);
            Entries = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.ListHistoryEntryDto) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            _log?.Invoke($"Ignoring unreadable history: {ex.Message}");
            Quarantine();
            Entries = [];
        }
    }

    public void Append(HistoryEntryDto entry)
    {
        Entries.Add(entry);
        var json = JsonSerializer.Serialize(Entries, ProtocolJsonContext.Default.ListHistoryEntryDto);
        DurableFile.Write(_path, Encoding.UTF8.GetBytes(json));
    }

    void Quarantine()
    {
        var dest = _path + ".bad";
        File.Move(_path, dest, overwrite: true);
    }
}

sealed class SettingsStore
{
    readonly string _path;
    readonly Action<string>? _log;

    public SettingsStore(string path, Action<string>? log)
    {
        _path = path;
        _log = log;
    }

    public SettingsDto Load()
    {
        if (!File.Exists(_path))
            return new SettingsDto();

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.SettingsDto) ?? new SettingsDto();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            _log?.Invoke($"Ignoring unreadable settings: {ex.Message}");
            File.Move(_path, _path + ".bad", overwrite: true);
            return new SettingsDto();
        }
    }

    public void Save(SettingsDto settings)
    {
        var json = JsonSerializer.Serialize(settings, ProtocolJsonContext.Default.SettingsDto);
        DurableFile.Write(_path, Encoding.UTF8.GetBytes(json));
    }
}
