using System.Text.RegularExpressions;

namespace MachineVoice.Core;

/// <summary>The short heading before a response: «Cursor, проект X.»</summary>
public static partial class SpeechHeading
{
    public const int MaxTopicWords = 12;

    public static string Of(string source, string? project)
    {
        var parts = new List<string> { SourceName(source) };
        if (!string.IsNullOrWhiteSpace(project))
            parts.Add("проект " + SpeechText.Collapse(project));
        return SpeechText.Sentence(string.Join(", ", parts));
    }

    /// <summary>The first sentence of the prompt, at most <see cref="MaxTopicWords"/> words.</summary>
    public static string? Topic(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var text = SpeechText.Collapse(PathShortener.Shorten(UrlShortener.Shorten(MarkdownProcessor.ToSpeech(prompt))));
        var end = SentenceEnd().Match(text);
        if (end.Success && end.Index > 0)
            text = text[..end.Index];

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cut = words.Length > MaxTopicWords;
        text = string.Join(' ', words.Take(MaxTopicWords)).TrimEnd(".,;:!?…".ToCharArray());
        if (text.Length == 0)
            return null;
        return cut ? text + "…" : text;
    }

    static string SourceName(string source)
    {
        var name = source.Trim().Replace('-', ' ').Replace('_', ' ');
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    [GeneratedRegex(@"[.!?…](?=\s|$)")]
    private static partial Regex SentenceEnd();
}
