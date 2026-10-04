using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MachineVoice.Core;

sealed class CursorTurnStore
{
    readonly string _path;
    readonly Action<string>? _log;

    public CursorTurnStore(string path, Action<string>? log)
    {
        _path = path;
        _log = log;
    }

    public Dictionary<string, CursorTurn> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, CursorTurn>(StringComparer.Ordinal);

        try
        {
            var json = File.ReadAllText(_path);
            var turns = JsonSerializer.Deserialize(json, CursorJsonContext.Default.DictionaryStringCursorTurn)
                ?? new Dictionary<string, CursorTurn>();
            return new Dictionary<string, CursorTurn>(turns, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            _log?.Invoke($"Ignoring unreadable cursor turns: {ex.Message}");
            File.Move(_path, _path + ".bad", overwrite: true);
            return new Dictionary<string, CursorTurn>(StringComparer.Ordinal);
        }
    }

    public void Save(Dictionary<string, CursorTurn> turns)
    {
        var json = JsonSerializer.Serialize(turns, CursorJsonContext.Default.DictionaryStringCursorTurn);
        DurableFile.Write(_path, Encoding.UTF8.GetBytes(json));
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, CursorTurn>))]
sealed partial class CursorJsonContext : JsonSerializerContext;
