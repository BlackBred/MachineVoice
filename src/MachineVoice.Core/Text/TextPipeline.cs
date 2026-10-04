namespace MachineVoice.Core;

public sealed record SpeechContext(string Source, string? Project, string? Topic);

/// <summary>One step that turns a response into text for TTS. Steps run in order, each gets the previous result.</summary>
public interface ITextProcessor
{
    ValueTask<string> ProcessAsync(string text, SpeechContext context, CancellationToken cancellationToken);
}

public sealed class TextPipeline(IReadOnlyList<ITextProcessor> processors)
{
    public IReadOnlyList<ITextProcessor> Processors { get; } = processors;

    /// <summary>Markdown to plain text, code blocks, URLs and paths, then the heading.</summary>
    public static TextPipeline Rules() => new(RuleSteps());

    /// <summary>LLM retelling first (it sees the original Markdown), then the same rules over its answer.</summary>
    public static TextPipeline WithSummary(ITextProcessor summarizer) => new([summarizer, .. RuleSteps()]);

    public async ValueTask<string> RunAsync(string text, SpeechContext context, CancellationToken cancellationToken = default)
    {
        foreach (var processor in Processors)
            text = await processor.ProcessAsync(text, context, cancellationToken).ConfigureAwait(false);
        return text;
    }

    static ITextProcessor[] RuleSteps() =>
    [
        new MarkdownProcessor(),
        new UrlShortener(),
        new PathShortener(),
        new HeadingProcessor(),
    ];
}
