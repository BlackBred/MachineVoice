namespace MachineVoice.Core;

static class SpeechText
{
    static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    public static string Collapse(string text) =>
        string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Collapses whitespace and ends the text with punctuation so TTS pauses after it.</summary>
    public static string Sentence(string text)
    {
        var collapsed = Collapse(text);
        if (collapsed.Length == 0)
            return collapsed;
        return ".!?…:;".Contains(collapsed[^1]) ? collapsed : collapsed + ".";
    }
}
