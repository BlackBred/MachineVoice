using System.Text.Json;
using System.Text.Json.Serialization;
using MachineVoice.Protocol;

namespace MachineVoice.Core;

/// <summary>
/// Saved voice samples in a directory: {id}.wav with {id}.json for the name and the transcript. Drafts wait in a
/// subdirectory until they are saved; the ones left from an earlier run are deleted. The built-in voices are written
/// to another subdirectory at start, because the server reads a sample from a file.
/// </summary>
public sealed class VoiceLibrary
{
    public const string DirectoryName = "voices";
    public const int MaxName = 64;
    const string DraftsName = "drafts";
    const string BuiltInName = "builtin";

    readonly object _gate = new();
    readonly string _directory;
    readonly string _drafts;
    readonly IReadOnlyList<VoiceDto> _builtIn;
    readonly Action<string>? _log;

    public VoiceLibrary(string directory, IReadOnlyList<BuiltInVoice>? builtIn = null, Action<string>? log = null)
    {
        _directory = directory;
        _drafts = Path.Combine(directory, DraftsName);
        _log = log;
        try
        {
            if (Directory.Exists(_drafts))
                Directory.Delete(_drafts, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Old voice drafts were not deleted: {ex.Message}");
        }

        _builtIn = Install(builtIn ?? []);
    }

    /// <summary>The built-in voices first, then the saved ones, oldest first.</summary>
    public IReadOnlyList<VoiceDto> List()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory))
                return _builtIn;
            return _builtIn.Concat(Directory.EnumerateFiles(_directory, "*.json")
                    .Select(path => Read(_directory, Path.GetFileNameWithoutExtension(path)))
                    .OfType<(VoiceDto Voice, DateTimeOffset Created)>()
                    .OrderBy(entry => entry.Created)
                    .ThenBy(entry => entry.Voice.Name, StringComparer.CurrentCulture)
                    .Select(entry => entry.Voice))
                .ToList();
        }
    }

    public VoiceDto? Find(string id)
    {
        if (!ValidId(id))
            return null;
        if (_builtIn.FirstOrDefault(voice => voice.Id == id) is { } builtIn)
            return builtIn;
        lock (_gate)
            return Read(_directory, id)?.Voice;
    }

    public VoiceDto AddDraft(byte[] wav, string text)
    {
        lock (_gate)
        {
            CreateDirectory(_directory);
            CreateDirectory(_drafts);
            var id = Guid.NewGuid().ToString("N")[..12];
            Write(_drafts, id, wav, new VoiceMeta { Text = text, Created = DateTimeOffset.UtcNow });
            return Read(_drafts, id)!.Value.Voice;
        }
    }

    /// <summary>Null when there is no such draft.</summary>
    public VoiceDto? Save(string draftId, string name)
    {
        if (!ValidId(draftId))
            return null;
        lock (_gate)
        {
            if (Read(_drafts, draftId) is not { } draft)
                return null;
            var meta = new VoiceMeta { Name = name.Trim(), Text = draft.Voice.Text, Created = DateTimeOffset.UtcNow };
            File.Move(Path.Combine(_drafts, draftId + ".wav"), Path.Combine(_directory, draftId + ".wav"), overwrite: true);
            WriteMeta(_directory, draftId, meta);
            File.Delete(Path.Combine(_drafts, draftId + ".json"));
            return Read(_directory, draftId)!.Value.Voice;
        }
    }

    public bool Delete(string id)
    {
        if (!ValidId(id))
            return false;
        lock (_gate)
        {
            var meta = Path.Combine(_directory, id + ".json");
            if (!File.Exists(meta))
                return false;
            File.Delete(meta);
            File.Delete(Path.Combine(_directory, id + ".wav"));
            return true;
        }
    }

    /// <summary>A sample that cannot be written is left out; samples of voices the app no longer has are deleted.</summary>
    List<VoiceDto> Install(IReadOnlyList<BuiltInVoice> voices)
    {
        var directory = Path.Combine(_directory, BuiltInName);
        var installed = new List<VoiceDto>();
        try
        {
            if (voices.Count > 0)
            {
                CreateDirectory(_directory);
                CreateDirectory(directory);
            }
            else if (!Directory.Exists(directory))
            {
                return installed;
            }

            foreach (var stale in Directory.EnumerateFiles(directory)
                         .Where(path => voices.All(voice => voice.Id + ".wav" != Path.GetFileName(path))))
                File.Delete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Built-in voices are not installed: {ex.Message}");
            return installed;
        }

        foreach (var voice in voices.Where(voice => ValidId(voice.Id)))
        {
            var audio = Path.Combine(directory, voice.Id + ".wav");
            try
            {
                using (var source = voice.Open())
                {
                    if (!File.Exists(audio) || new FileInfo(audio).Length != source.Length)
                    {
                        var temp = audio + ".tmp";
                        using (var target = File.Create(temp))
                            source.CopyTo(target);
                        AppLayout.SetPrivateFile(temp);
                        File.Move(temp, audio, overwrite: true);
                    }
                }

                installed.Add(new VoiceDto { Id = voice.Id, Name = voice.Name, AudioPath = audio, Text = voice.Text, BuiltIn = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _log?.Invoke($"Built-in voice {voice.Id} is not installed: {ex.Message}");
            }
        }

        return installed;
    }

    static bool ValidId(string id) =>
        id.Length is > 0 and <= 32 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    (VoiceDto Voice, DateTimeOffset Created)? Read(string directory, string id)
    {
        var audio = Path.Combine(directory, id + ".wav");
        var metaPath = Path.Combine(directory, id + ".json");
        if (!ValidId(id) || !File.Exists(audio) || !File.Exists(metaPath))
            return null;
        try
        {
            var meta = JsonSerializer.Deserialize(File.ReadAllText(metaPath), VoiceJsonContext.Default.VoiceMeta);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Text))
                return null;
            return (new VoiceDto { Id = id, Name = meta.Name, AudioPath = audio, Text = meta.Text }, meta.Created);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log?.Invoke($"Voice {id} is not readable: {ex.Message}");
            return null;
        }
    }

    static void Write(string directory, string id, byte[] wav, VoiceMeta meta)
    {
        var audio = Path.Combine(directory, id + ".wav");
        File.WriteAllBytes(audio, wav);
        AppLayout.SetPrivateFile(audio);
        WriteMeta(directory, id, meta);
    }

    static void WriteMeta(string directory, string id, VoiceMeta meta)
    {
        var path = Path.Combine(directory, id + ".json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, VoiceJsonContext.Default.VoiceMeta));
        AppLayout.SetPrivateFile(temp);
        File.Move(temp, path, overwrite: true);
    }

    static void CreateDirectory(string path)
    {
        if (Directory.Exists(path))
            return;
        Directory.CreateDirectory(path);
        AppLayout.SetPrivateDirectory(path);
    }
}

sealed class VoiceMeta
{
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    public DateTimeOffset Created { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VoiceMeta))]
sealed partial class VoiceJsonContext : JsonSerializerContext;
