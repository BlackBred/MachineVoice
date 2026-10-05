namespace MachineVoice.Core;

/// <param name="FirstWord">Index of the first word of the chunk in the whole text.</param>
/// <param name="Words">Runs of letters and digits, counted the same way as the client's progress estimate.</param>
public sealed record SpeechChunk(string Text, int FirstWord, IReadOnlyList<string> Words);

/// <summary>
/// Splits speech into sentences for a neural engine. Synthesis runs slightly faster than real time, so the next
/// chunk is ready while the current one plays only if chunks stay short; a short first chunk starts speech sooner.
/// </summary>
public static class SpeechChunks
{
    public const int FirstLimit = 100;
    public const int Limit = 220;

    public static IReadOnlyList<SpeechChunk> Split(string text)
    {
        var pieces = new List<string>();
        foreach (var sentence in Sentences(text))
        {
            foreach (var piece in Bounded(sentence, pieces.Count == 0 ? FirstLimit : Limit))
                pieces.Add(piece);
        }

        var chunks = new List<SpeechChunk>(pieces.Count);
        var firstWord = 0;
        string? pending = null;
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pending is null ? pieces[i] : pending + " " + pieces[i];
            pending = null;
            var words = Words(piece);
            if (words.Count == 0)
                continue;

            // A lone list number or a one-word fragment sounds better together with what follows.
            if (words.Count == 1 && i + 1 < pieces.Count && piece.Length + 1 + pieces[i + 1].Length <= Limit)
            {
                pending = piece;
                continue;
            }

            chunks.Add(new SpeechChunk(piece, firstWord, words));
            firstWord += words.Count;
        }

        return chunks;
    }

    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inWord = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(text[start..i]);
                start = -1;
            }
        }

        return words;
    }

    static IEnumerable<string> Sentences(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int end;
            if (text[i] == '\n')
            {
                end = i + 1;
            }
            else if (text[i] is '.' or '!' or '?' or '…')
            {
                end = i + 1;
                while (end < text.Length && text[end] is '.' or '!' or '?' or '…' or '"' or '»' or '”' or ')' or '\'')
                    end++;
                if (end < text.Length && !char.IsWhiteSpace(text[end]))
                    continue;
            }
            else
            {
                continue;
            }

            var sentence = text[start..end].Trim();
            if (sentence.Length > 0)
                yield return sentence;
            start = end;
            i = end - 1;
        }

        var rest = text[start..].Trim();
        if (rest.Length > 0)
            yield return rest;
    }

    static IEnumerable<string> Bounded(string sentence, int limit)
    {
        var rest = sentence;
        while (rest.Length > limit)
        {
            var cut = BreakBefore(rest, limit);
            var head = rest[..cut].Trim();
            if (head.Length > 0)
                yield return head;
            rest = rest[cut..].Trim();
        }

        if (rest.Length > 0)
            yield return rest;
    }

    /// <summary>A clause boundary in the second half of the limit, otherwise the last space before it.</summary>
    static int BreakBefore(string text, int limit)
    {
        for (var i = limit; i > limit / 2; i--)
        {
            if (text[i - 1] is ',' or ';' or ':' or '—' or '–' && char.IsWhiteSpace(text[i]))
                return i;
        }

        for (var i = limit; i > 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
                return i;
        }

        return char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
    }
}
