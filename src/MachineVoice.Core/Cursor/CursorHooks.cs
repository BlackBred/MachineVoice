using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

readonly record struct CursorInstallation(SourceConnectionStatus Status, bool LegacySpeaker)
{
    public SourceStatusDto ToDto() => new()
    {
        Name = CursorHooks.SourceName,
        Status = Status,
        LegacySpeaker = LegacySpeaker,
    };
}

/// <summary>
/// Registers hook.sh in ~/.cursor/hooks.json. Entries that are not MachineVoice are left in place,
/// the same way cursor-speaker.sh only rewrites its own commands.
/// </summary>
sealed class CursorHooks
{
    public const string SourceName = "cursor";
    public const int HookTimeoutSeconds = 5;

    static readonly string[] Events = ["beforeSubmitPrompt", "afterAgentResponse", "stop"];
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    readonly string _hooksPath;
    readonly string _hookScriptPath;
    readonly byte[] _script;
    readonly Action<string>? _log;
    readonly string _command;

    public CursorHooks(string cursorDirectory, string hookScriptPath, byte[] script, Action<string>? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookScriptPath);
        _hooksPath = Path.Combine(cursorDirectory, "hooks.json");
        _hookScriptPath = Path.GetFullPath(hookScriptPath);
        _script = script;
        _log = log;
        _command = Quote(_hookScriptPath);
    }

    public static bool IsCursor(string source) =>
        string.Equals(source, SourceName, StringComparison.Ordinal);

    public CursorInstallation Inspect()
    {
        try
        {
            return InspectCore();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            _log?.Invoke($"Cursor hooks status unreadable: {ex.Message}");
            return new CursorInstallation(SourceConnectionStatus.Stale, false);
        }
    }

    public CursorInstallation Install()
    {
        WriteScript();
        var directory = Path.GetDirectoryName(_hooksPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        JsonObject root;
        if (File.Exists(_hooksPath))
        {
            root = Read();
            Backup();
        }
        else
        {
            root = new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject(),
            };
        }

        if (root["version"] is null)
            root["version"] = 1;
        if (root["hooks"] is null)
            root["hooks"] = new JsonObject();
        if (root["hooks"] is not JsonObject)
            throw new InvalidDataException("hooks is not an object.");

        foreach (var name in Events)
            Upsert(root, name);
        Write(root);
        return Inspect();
    }

    public CursorInstallation Uninstall()
    {
        if (!File.Exists(_hooksPath))
            return Inspect();

        var root = Read();
        if (!RemoveOurs(root))
            return Inspect();

        Backup();
        if (IsDisposable(root))
        {
            File.Delete(_hooksPath);
            var bak = _hooksPath + ".bak";
            if (File.Exists(bak))
                File.Delete(bak);
        }
        else
        {
            Write(root);
        }

        return Inspect();
    }

    CursorInstallation InspectCore()
    {
        var commands = Commands().ToArray();
        var legacy = commands.Any(IsLegacySpeaker);
        var ours = commands.Where(IsManaged).ToArray();
        if (ours.Length == 0)
            return new CursorInstallation(SourceConnectionStatus.Disconnected, legacy);

        var hooks = File.Exists(_hooksPath) ? Read() : null;
        var events = hooks?["hooks"] as JsonObject;
        var allExact = events is not null && Events.All(name => EventHasExact(events, name));
        var anyStale = ours.Any(command => !string.Equals(command, _command, StringComparison.Ordinal));
        var status = allExact && !anyStale && ScriptMatches()
            ? SourceConnectionStatus.Connected
            : SourceConnectionStatus.Stale;
        return new CursorInstallation(status, legacy);
    }

    void Upsert(JsonObject root, string eventName)
    {
        var hooks = (JsonObject)root["hooks"]!;
        var kept = new JsonArray();
        if (hooks[eventName] is JsonArray existing)
        {
            foreach (var item in existing)
            {
                if (IsManaged(CommandOf(item)) || item is null)
                    continue;
                kept.Add(item.DeepClone());
            }
        }

        kept.Add((JsonNode)new JsonObject
        {
            ["command"] = _command,
            ["timeout"] = HookTimeoutSeconds,
        });
        hooks[eventName] = kept;
    }

    bool RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
            return false;

        var changed = false;
        foreach (var name in hooks.Select(pair => pair.Key).ToArray())
        {
            if (hooks[name] is not JsonArray list)
                continue;

            var kept = new JsonArray();
            var removed = false;
            foreach (var item in list)
            {
                if (IsManaged(CommandOf(item)))
                {
                    removed = true;
                    continue;
                }

                if (item is not null)
                    kept.Add(item.DeepClone());
            }

            if (!removed)
                continue;

            changed = true;
            if (kept.Count == 0)
                hooks.Remove(name);
            else
                hooks[name] = kept;
        }

        return changed;
    }

    bool EventHasExact(JsonObject hooks, string name)
    {
        if (hooks[name] is not JsonArray list)
            return false;
        return list.Any(item => string.Equals(CommandOf(item), _command, StringComparison.Ordinal));
    }

    IEnumerable<string> Commands()
    {
        if (!File.Exists(_hooksPath))
            yield break;

        if (Read()["hooks"] is not JsonObject hooks)
            yield break;

        foreach (var pair in hooks)
        {
            if (pair.Value is not JsonArray list)
                continue;
            foreach (var item in list)
            {
                var command = CommandOf(item);
                if (command is not null)
                    yield return command;
            }
        }
    }

    bool ScriptMatches()
    {
        if (!File.Exists(_hookScriptPath))
            return false;
        if (!File.ReadAllBytes(_hookScriptPath).AsSpan().SequenceEqual(_script))
            return false;
        if (OperatingSystem.IsWindows())
            return true;
        var mode = File.GetUnixFileMode(_hookScriptPath);
        return (mode & UnixFileMode.UserExecute) != 0;
    }

    void WriteScript()
    {
        var directory = Path.GetDirectoryName(_hookScriptPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        DurableFile.Write(_hookScriptPath, _script);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_hookScriptPath, AppLayout.ExecutableFile);
    }

    JsonObject Read()
    {
        var node = JsonNode.Parse(File.ReadAllText(_hooksPath))
            ?? throw new InvalidDataException("Empty hooks.json.");
        if (node is not JsonObject root)
            throw new InvalidDataException("hooks.json is not an object.");
        return root;
    }

    void Backup() => File.Copy(_hooksPath, _hooksPath + ".bak", overwrite: true);

    void Write(JsonObject root)
    {
        var tmp = _hooksPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Pretty) + "\n", Utf8);
        File.Move(tmp, _hooksPath, overwrite: true);
    }

    static bool IsDisposable(JsonObject root)
    {
        foreach (var prop in root)
        {
            if (prop.Key is not ("version" or "hooks"))
                return false;
        }

        return root["hooks"] is not JsonObject hooks || hooks.Count == 0;
    }

    static string? CommandOf(JsonNode? node)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("command", out var value) || value is null)
            return null;
        try
        {
            return value.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static string Quote(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    bool IsManaged(string? command) =>
        string.Equals(command, _command, StringComparison.Ordinal) || LooksLikeMachineVoiceHook(command);

    static bool LooksLikeMachineVoiceHook(string? command)
    {
        var path = Unquote(command);
        return path.Contains("MachineVoice", StringComparison.Ordinal)
            && (path.EndsWith("/hook.sh", StringComparison.Ordinal) || path.EndsWith("\\hook.sh", StringComparison.Ordinal));
    }

    static bool IsLegacySpeaker(string? command)
    {
        var path = Unquote(command);
        return path.EndsWith("/speak.sh", StringComparison.Ordinal) || path.EndsWith("\\speak.sh", StringComparison.Ordinal);
    }

    static string Unquote(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "";
        var text = command.Trim();
        if (text.Length >= 2 && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"')))
            text = text[1..^1];
        return text;
    }
}
