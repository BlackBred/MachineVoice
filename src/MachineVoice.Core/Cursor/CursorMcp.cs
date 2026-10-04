using System.Text.Json;
using System.Text.Json.Nodes;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Registers the bundled MCP server in ~/.cursor/mcp.json. The binary is copied next to the sockets and mcp.json
/// points at the copy, so the entry survives moving or reinstalling the app. Only the "machinevoice" entry is
/// edited; the bytes of every other entry stay as they were.
/// </summary>
sealed class CursorMcp
{
    public const string ServerName = "machinevoice";
    const string ServersKey = "mcpServers";

    readonly string _configPath;
    readonly string _installedPath;
    readonly string? _bundledPath;
    readonly string _socketPath;
    readonly Action<string>? _log;

    public CursorMcp(string cursorDirectory, string installedPath, string? bundledPath, string socketPath, Action<string>? log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(installedPath);
        _configPath = Path.Combine(cursorDirectory, "mcp.json");
        _installedPath = Path.GetFullPath(installedPath);
        _bundledPath = string.IsNullOrWhiteSpace(bundledPath) ? null : Path.GetFullPath(bundledPath);
        _socketPath = Path.GetFullPath(socketPath);
        _log = log;
    }

    bool Available => _bundledPath is not null && File.Exists(_bundledPath);

    public McpStatusDto Inspect()
    {
        try
        {
            return Status(InspectCore());
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            _log?.Invoke($"Cursor MCP status unreadable: {ex.Message}");
            return Status(SourceConnectionStatus.Stale);
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> without a bundled binary.</summary>
    public McpStatusDto Install()
    {
        if (!Available)
            throw new InvalidOperationException("The MCP server binary is not bundled with this build.");

        // The new file is computed before anything is written: an unreadable mcp.json stops the install untouched.
        var exists = File.Exists(_configPath);
        var updated = exists ? WithEntry(File.ReadAllBytes(_configPath)) : NewFile();

        CopyBinary();
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        if (exists)
            Backup();
        Write(updated);
        return Inspect();
    }

    public McpStatusDto Uninstall()
    {
        if (File.Exists(_configPath))
        {
            var data = JsonObjectText.StripBom(File.ReadAllBytes(_configPath), out var bom);
            var root = JsonObjectText.ReadRoot(data);
            if (root.Find(ServersKey) is { } serversMember
                && JsonObjectText.ReadValue(data, serversMember) is { } servers
                && servers.Find(ServerName) is { } entry)
            {
                Backup();
                if (root.Members.Count == 1 && servers.Members.Count == 1)
                {
                    File.Delete(_configPath);
                    File.Delete(_configPath + ".bak");
                }
                else
                {
                    Write(JsonObjectText.AddBom(servers.Remove(data, entry), bom));
                }
            }
        }

        if (File.Exists(_installedPath))
            File.Delete(_installedPath);
        return Inspect();
    }

    SourceConnectionStatus InspectCore()
    {
        if (!File.Exists(_configPath))
            return SourceConnectionStatus.Disconnected;

        var data = JsonObjectText.StripBom(File.ReadAllBytes(_configPath), out _);
        var root = JsonObjectText.ReadRoot(data);
        if (root.Find(ServersKey) is not { } serversMember)
            return SourceConnectionStatus.Disconnected;
        var servers = JsonObjectText.ReadValue(data, serversMember)
            ?? throw new InvalidDataException($"{ServersKey} is not an object.");
        if (servers.Find(ServerName) is not { } entry)
            return SourceConnectionStatus.Disconnected;

        return JsonNode.DeepEquals(JsonObjectText.ParseValue(data, entry), Entry()) && BinaryMatches()
            ? SourceConnectionStatus.Connected
            : SourceConnectionStatus.Stale;
    }

    McpStatusDto Status(SourceConnectionStatus status) => new() { Status = status, Available = Available };

    JsonObject Entry() => new()
    {
        ["command"] = _installedPath,
        ["args"] = new JsonArray("--socket", _socketPath),
    };

    byte[] WithEntry(byte[] file)
    {
        var data = JsonObjectText.StripBom(file, out var bom);
        var root = JsonObjectText.ReadRoot(data);
        byte[] updated;
        if (root.Find(ServersKey) is not { } serversMember)
        {
            updated = root.Insert(data, ServersKey, new JsonObject { [ServerName] = Entry() });
        }
        else
        {
            var servers = JsonObjectText.ReadValue(data, serversMember)
                ?? throw new InvalidDataException($"{ServersKey} is not an object.");
            updated = servers.Find(ServerName) is { } entry
                ? JsonObjectText.ReplaceValue(data, entry, Entry())
                : servers.Insert(data, ServerName, Entry());
        }

        return JsonObjectText.AddBom(updated, bom);
    }

    byte[] NewFile()
    {
        var empty = "{}"u8.ToArray();
        var withEntry = JsonObjectText.ReadRoot(empty).Insert(empty, ServersKey, new JsonObject { [ServerName] = Entry() });
        return [.. withEntry, (byte)'\n'];
    }

    /// <summary>The copy must equal the bundled binary: an older copy means the app was updated after connecting.</summary>
    bool BinaryMatches()
    {
        if (!File.Exists(_installedPath))
            return false;
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(_installedPath) & UnixFileMode.UserExecute) == 0)
            return false;
        if (!Available)
            return true;
        return SameContent(_installedPath, _bundledPath!);
    }

    void CopyBinary()
    {
        if (File.Exists(_installedPath) && SameContent(_installedPath, _bundledPath!))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_installedPath, AppLayout.ExecutableFile);
            return;
        }

        DurableFile.Write(_installedPath, File.ReadAllBytes(_bundledPath!));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_installedPath, AppLayout.ExecutableFile);
    }

    static bool SameContent(string a, string b)
    {
        var left = new FileInfo(a);
        var right = new FileInfo(b);
        if (left.Length != right.Length)
            return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    void Backup() => File.Copy(_configPath, _configPath + ".bak", overwrite: true);

    void Write(byte[] data)
    {
        var tmp = _configPath + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, _configPath, overwrite: true);
    }
}
