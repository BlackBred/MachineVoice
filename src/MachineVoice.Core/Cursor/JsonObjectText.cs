using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MachineVoice.Core;

/// <summary>
/// A JSON object as byte ranges of its members. Edits splice one member and keep the bytes of the others,
/// including their formatting and comments.
/// </summary>
sealed class JsonObjectText
{
    public static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    JsonObjectText(int open, int close, List<Member> members)
    {
        Open = open;
        Close = close;
        Members = members;
    }

    /// <summary>Index of '{'.</summary>
    public int Open { get; }

    /// <summary>Index of '}'.</summary>
    public int Close { get; }

    public IReadOnlyList<Member> Members { get; }

    public sealed record Member(string Name, int NameStart, int ValueStart, int ValueEnd);

    /// <summary>Reads the root object of a whole document. The document must not start with a BOM.</summary>
    public static JsonObjectText ReadRoot(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException("The root is not an object.");
        var root = Read(ref reader, 0);
        if (reader.Read())
            throw new InvalidDataException("Unexpected data after the root object.");
        return root;
    }

    /// <summary>Reads the object value of <paramref name="member"/>; null when the value is not an object.</summary>
    public static JsonObjectText? ReadValue(ReadOnlySpan<byte> json, Member member)
    {
        var reader = new Utf8JsonReader(json[member.ValueStart..member.ValueEnd], ReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;
        return Read(ref reader, member.ValueStart);
    }

    public static JsonNode? ParseValue(ReadOnlySpan<byte> json, Member member) =>
        JsonNode.Parse(json[member.ValueStart..member.ValueEnd], documentOptions: DocumentOptions);

    /// <summary>The only member with this name. Duplicates make the file ambiguous, so they are an error.</summary>
    public Member? Find(string name)
    {
        Member? found = null;
        foreach (var member in Members)
        {
            if (!string.Equals(member.Name, name, StringComparison.Ordinal))
                continue;
            if (found is not null)
                throw new InvalidDataException($"Duplicate \"{name}\".");
            found = member;
        }

        return found;
    }

    public static byte[] StripBom(byte[] data, out bool hadBom)
    {
        hadBom = data.AsSpan().StartsWith(Bom);
        return hadBom ? data[Bom.Length..] : data;
    }

    public static byte[] AddBom(byte[] data, bool bom) => bom ? [.. Bom, .. data] : data;

    public static byte[] ReplaceValue(byte[] json, Member member, JsonNode value)
    {
        var style = Style.Of(json);
        var text = StartsLine(json, member.NameStart)
            ? style.Format(value, LineIndent(json, member.NameStart))
            : Style.Compact(value);
        return Splice(json, member.ValueStart, member.ValueEnd, text);
    }

    public byte[] Insert(byte[] json, string name, JsonNode value)
    {
        var style = Style.Of(json);
        var key = JsonValue.Create(name).ToJsonString(Style.Relaxed);
        if (Members.Count > 0)
        {
            var first = Members[0];
            var last = Members[^1];
            if (!StartsLine(json, first.NameStart))
                return Splice(json, last.ValueEnd, last.ValueEnd, $", {key}: {Style.Compact(value)}");

            var indent = LineIndent(json, first.NameStart);
            return Splice(json, last.ValueEnd, last.ValueEnd, $",{style.NewLine}{indent}{key}: {style.Format(value, indent)}");
        }

        var braceIndent = LineIndent(json, Open);
        var memberIndent = braceIndent + style.Unit;
        var member = $"{style.NewLine}{memberIndent}{key}: {style.Format(value, memberIndent)}";
        return IsBlank(json, Open + 1, Close)
            ? Splice(json, Open + 1, Close, member + style.NewLine + braceIndent)
            : Splice(json, Open + 1, Open + 1, member);
    }

    public byte[] Remove(byte[] json, Member member)
    {
        var index = IndexOf(member);
        if (Members.Count == 1)
            return Splice(json, Open + 1, Close, "");
        if (index < Members.Count - 1)
            return Splice(json, member.NameStart, Members[index + 1].NameStart, "");
        return Splice(json, Members[index - 1].ValueEnd, member.ValueEnd, "");
    }

    int IndexOf(Member member)
    {
        for (var i = 0; i < Members.Count; i++)
        {
            if (ReferenceEquals(Members[i], member))
                return i;
        }

        throw new ArgumentException("The member does not belong to this object.", nameof(member));
    }

    static JsonObjectText Read(ref Utf8JsonReader reader, int offset)
    {
        var open = offset + (int)reader.TokenStartIndex;
        var members = new List<Member>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new JsonObjectText(open, offset + (int)reader.TokenStartIndex, members);

            var name = reader.GetString()!;
            var nameStart = offset + (int)reader.TokenStartIndex;
            if (!reader.Read())
                break;
            var valueStart = offset + (int)reader.TokenStartIndex;
            reader.Skip();
            members.Add(new Member(name, nameStart, valueStart, offset + (int)reader.BytesConsumed));
        }

        throw new InvalidDataException("Unterminated object.");
    }

    static byte[] Splice(byte[] json, int start, int end, string text)
    {
        var insert = Encoding.UTF8.GetBytes(text);
        var result = new byte[json.Length - (end - start) + insert.Length];
        json.AsSpan(0, start).CopyTo(result);
        insert.CopyTo(result.AsSpan(start));
        json.AsSpan(end).CopyTo(result.AsSpan(start + insert.Length));
        return result;
    }

    static int LineStart(byte[] json, int index)
    {
        var i = index;
        while (i > 0 && json[i - 1] != (byte)'\n')
            i--;
        return i;
    }

    static bool StartsLine(byte[] json, int index) => IsBlank(json, LineStart(json, index), index);

    /// <summary>Leading spaces and tabs of the line that contains <paramref name="index"/>.</summary>
    static string LineIndent(byte[] json, int index)
    {
        var start = LineStart(json, index);
        var end = start;
        while (end < index && json[end] is (byte)' ' or (byte)'\t')
            end++;
        return Encoding.ASCII.GetString(json, start, end - start);
    }

    static bool IsBlank(byte[] json, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (json[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                return false;
        }

        return true;
    }

    sealed class Style
    {
        public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public required string NewLine { get; init; }
        public required string Unit { get; init; }

        /// <summary>Line ending and indent unit of an existing document; LF and two spaces for a new one.</summary>
        public static Style Of(byte[] json)
        {
            var newLine = json.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
            var unit = "  ";
            var lineStart = json.AsSpan().IndexOf((byte)'\n') + 1;
            if (lineStart > 0)
            {
                var end = lineStart;
                while (end < json.Length && json[end] is (byte)' ' or (byte)'\t')
                    end++;
                if (end > lineStart)
                    unit = Encoding.ASCII.GetString(json, lineStart, end - lineStart);
            }

            return new Style { NewLine = newLine, Unit = unit };
        }

        public static string Compact(JsonNode value) => value.ToJsonString(Relaxed);

        /// <summary>Indented value whose first line continues the current one and the rest start at <paramref name="indent"/>.</summary>
        public string Format(JsonNode value, string indent)
        {
            var options = new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                WriteIndented = true,
                NewLine = "\n",
                IndentCharacter = Unit[0],
                IndentSize = Unit.Length,
            };
            return value.ToJsonString(options).Replace("\n", NewLine + indent, StringComparison.Ordinal);
        }
    }
}
