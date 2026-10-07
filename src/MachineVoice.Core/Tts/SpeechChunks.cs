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
    /// <summary>
    /// A third past the original 100: at 100 the first clip finished before the next one was synthesized.
    /// </summary>
    public const int FirstLimit = 133;
    public const int Limit = 220;

    /// <param name="pack">
    /// Join sentences into chunks up to the limits, for a synthesizer that pays a fixed cost for every request.
    /// </param>
    public static IReadOnlyList<SpeechChunk> Split(string text, bool pack = false)
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
            var piece = pending is null ? pieces[i] : Join(pending, pieces[i]);
            pending = null;
            var words = Words(piece);
            if (words.Count == 0)
                continue;

            // A lone list number or a one-word fragment sounds better together with what follows.
            if (words.Count == 1 && i + 1 < pieces.Count && piece.Length + 2 + pieces[i + 1].Length <= Limit)
            {
                pending = piece;
                continue;
            }

            chunks.Add(new SpeechChunk(piece, firstWord, words));
            firstWord += words.Count;
        }

        return pack ? Pack(chunks) : chunks;
    }

    static List<SpeechChunk> Pack(List<SpeechChunk> chunks)
    {
        var packed = new List<SpeechChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (packed.Count > 0)
            {
                var last = packed[^1];
                var text = Join(last.Text, chunk.Text);
                if (text.Length <= (packed.Count == 1 ? FirstLimit : Limit))
                {
                    packed[^1] = new SpeechChunk(text, last.FirstWord, [.. last.Words, .. chunk.Words]);
                    continue;
                }
            }

            packed.Add(chunk);
        }

        return packed;
    }

    // A heading or a list item has no full stop; without one the voice runs it into the next sentence.
    static string Join(string first, string second) =>
        char.IsLetterOrDigit(first[^1]) ? first + ". " + second : first + " " + second;

    public static List<string> Words(string text) => WordRanges(text).Select(range => text.Substring(range.Start, range.Length)).ToList();

    /// <summary>
    /// When each word of <paramref name="text"/> starts, from the word marks of a synthesizer: the character
    /// offset of a mark and its time in seconds. A word without a mark starts with the one before it.
    /// </summary>
    public static double[] WordStarts(string text, IEnumerable<(int Location, double Seconds)> marks)
    {
        var ranges = WordRanges(text);
        var starts = new double[ranges.Count];
        var known = new bool[ranges.Count];
        foreach (var (location, seconds) in marks)
        {
            // A mark may start at punctuation before its word, such as an opening quote.
            var word = ranges.FindIndex(range => range.Start + range.Length > location);
            if (word >= 0 && !known[word])
            {
                starts[word] = seconds;
                known[word] = true;
            }
        }

        for (var i = 1; i < starts.Length; i++)
            starts[i] = known[i] ? Math.Max(starts[i], starts[i - 1]) : starts[i - 1];
        return starts;
    }

    static List<(int Start, int Length)> WordRanges(string text)
    {
        var words = new List<(int, int)>();
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
                words.Add((start, i - start));
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
