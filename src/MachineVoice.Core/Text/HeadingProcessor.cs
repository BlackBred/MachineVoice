using System.Text.RegularExpressions;

namespace MachineVoice.Core;

/// <summary>Puts a short heading before the text: «Cursor, проект X, тема: Y.»</summary>
public sealed partial class HeadingProcessor : ITextProcessor
{
    public const int MaxTopicWords = 12;

    public ValueTask<string> ProcessAsync(string text, SpeechContext context, CancellationToken cancellationToken)
    {
        var heading = Heading(context);
        return ValueTask.FromResult(text.Length == 0 ? heading : heading + "\n" + text);
    }

    public static string Heading(SpeechContext context)
    {
        var parts = new List<string> { SourceName(context.Source) };
        if (!string.IsNullOrWhiteSpace(context.Project))
            parts.Add("проект " + SpeechText.Collapse(context.Project));

        var topic = Topic(context.Topic);
        if (topic is not null)
            parts.Add("тема: " + topic);
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
