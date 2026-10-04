using System.Text.RegularExpressions;

namespace MachineVoice.Core;

/// <summary>Replaces a URL with its host: https://github.com/org/repo/pull/1 becomes github.com.</summary>
public sealed partial class UrlShortener : ITextProcessor
{
    const string TrailingPunctuation = ".,;:!?";

    public ValueTask<string> ProcessAsync(string text, SpeechContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Shorten(text));

    public static string Shorten(string text) => UrlPattern().Replace(text, match =>
    {
        var url = match.Value.TrimEnd(TrailingPunctuation.ToCharArray());
        var tail = match.Value[url.Length..];
        var absolute = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + url : url;
        if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri))
            return match.Value;

        if (uri.IsFile)
            return PathShortener.Shorten(uri.LocalPath) + tail;
        if (string.IsNullOrEmpty(uri.Host))
            return match.Value;

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host + tail;
    });

    [GeneratedRegex("""\b(?:(?:https?|ftp|file)://|www\.)[^\s<>"'`()\[\]{}]+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}

/// <summary>
/// Replaces a file path with its last segment: /Users/me/src/App/Program.cs:12 becomes Program.cs.
/// Short slash pairs such as "и/или" or "TCP/IP" stay as they are.
/// </summary>
public sealed partial class PathShortener : ITextProcessor
{
    public ValueTask<string> ProcessAsync(string text, SpeechContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Shorten(text));

    public static string Shorten(string text) => PathPattern().Replace(text, match =>
    {
        var path = match.Groups["path"].Value;
        var trimmed = path.TrimEnd('.');
        var tail = match.Groups["line"].Success ? "" : path[trimmed.Length..];
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return match.Value;

        var name = segments[^1];
        if (name is "~" or "." or "..")
            return match.Value;

        var anchored = trimmed.StartsWith('/') || trimmed.StartsWith("~/") || trimmed.StartsWith("./") || trimmed.StartsWith("../");
        var looksLikePath = anchored || segments.Length >= 3 || FileExtension().IsMatch(name);
        return looksLikePath ? name + tail : match.Value;
    });

    [GeneratedRegex("""(?<![\w.~/@+-])(?=[\w.~@+-]*/)(?<path>(?:(?:~|\.{1,2})?/)?[\w.@+-]+(?:/[\w.@+-]+)*/?)(?<line>:\d+(?:[:-]\d+)?)?""")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"\.[A-Za-z][A-Za-z0-9]{0,9}$")]
    private static partial Regex FileExtension();
}
