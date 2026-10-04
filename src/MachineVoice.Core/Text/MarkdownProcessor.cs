using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MachineVoice.Core;

/// <summary>
/// Renders Markdown as plain sentences: one line per paragraph, heading, list item or table row.
/// Code blocks become <see cref="CodePlaceholder"/>, links keep their label, images and HTML are dropped.
/// </summary>
public sealed class MarkdownProcessor : ITextProcessor
{
    public const string CodePlaceholder = "Тут пример кода.";

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .UseTaskLists()
        .UseEmphasisExtras()
        .Build();

    public ValueTask<string> ProcessAsync(string text, SpeechContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToSpeech(text));

    public static string ToSpeech(string markdown)
    {
        var lines = new List<string>();
        WriteBlocks(Markdown.Parse(markdown, Pipeline), lines);
        return string.Join('\n', lines);
    }

    static void WriteBlocks(ContainerBlock container, List<string> lines)
    {
        foreach (var block in container)
            WriteBlock(block, lines);
    }

    static void WriteBlock(Block block, List<string> lines)
    {
        switch (block)
        {
            case CodeBlock:
                if (lines.Count == 0 || lines[^1] != CodePlaceholder)
                    lines.Add(CodePlaceholder);
                break;
            case Table table:
                foreach (var row in table.OfType<TableRow>())
                    Add(lines, string.Join(", ", row.OfType<TableCell>().Select(CellText).Where(cell => cell.Length > 0)));
                break;
            case HtmlBlock or ThematicBreakBlock or LinkReferenceDefinitionGroup or BlankLineBlock:
                break;
            case ContainerBlock container:
                WriteBlocks(container, lines);
                break;
            case LeafBlock { Inline: not null } leaf:
                Add(lines, InlineText(leaf.Inline));
                break;
        }
    }

    static string CellText(TableCell cell)
    {
        var lines = new List<string>();
        WriteBlocks(cell, lines);
        return string.Join(' ', lines.Select(line => line.TrimEnd('.'))).Trim();
    }

    static void Add(List<string> lines, string text)
    {
        var sentence = SpeechText.Sentence(text);
        if (sentence.Length > 0)
            lines.Add(sentence);
    }

    static string InlineText(ContainerInline container)
    {
        var builder = new StringBuilder();
        WriteInlines(container, builder);
        return builder.ToString();
    }

    static void WriteInlines(ContainerInline container, StringBuilder builder)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            WriteInline(child, builder);
    }

    static void WriteInline(Inline inline, StringBuilder builder)
    {
        switch (inline)
        {
            case LiteralInline literal:
                builder.Append(literal.Content.ToString());
                break;
            case CodeInline code:
                builder.Append(code.Content);
                break;
            case AutolinkInline link:
                builder.Append(link.Url);
                break;
            case LinkInline { IsImage: true }:
                break;
            case LinkInline link:
                var label = InlineText(link);
                builder.Append(string.IsNullOrWhiteSpace(label) ? link.Url : label);
                break;
            case LineBreakInline:
                builder.Append(' ');
                break;
            case HtmlEntityInline entity:
                builder.Append(entity.Transcoded.ToString());
                break;
            case HtmlInline or TaskList:
                break;
            case ContainerInline container:
                WriteInlines(container, builder);
                break;
        }
    }
}
