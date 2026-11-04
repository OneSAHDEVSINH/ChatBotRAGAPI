using System.Text;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Recursive structure-aware chunker. Text is split at the coarsest boundary that makes pieces fit
/// (paragraph, then line, then sentence, then word, then a hard cut as a last resort), and the pieces are
/// packed greedily into chunks of at most <c>chunkSize</c> characters. Overlap is made of whole trailing
/// pieces (e.g. the last sentence) of up to <c>overlap</c> characters, so chunks never start or end mid-word
/// unless a single word is longer than <c>chunkSize</c>.
/// </summary>
public sealed class TextChunker
{
    // Zero-width split points placed AFTER each delimiter, so concatenating the pieces reproduces the input.
    private static readonly Regex[] Boundaries =
    [
        new(@"(?<=\n[ \t]*\n)", RegexOptions.Compiled),   // paragraph (blank line)
        new(@"(?<=\n)", RegexOptions.Compiled),           // line
        new(@"(?<=[.!?;:][""')\]]?\s)", RegexOptions.Compiled), // sentence / clause
        new(@"(?<=\s)", RegexOptions.Compiled),           // word
    ];

    public IReadOnlyList<string> Chunk(string input, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be positive");
        if (overlap < 0 || chunkSize <= overlap) throw new ArgumentException("chunkSize must be > overlap >= 0");

        var text = input.Replace("\r\n", "\n").Replace('\r', '\n');
        var pieces = new List<string>();
        SplitToFit(text, 0, chunkSize, pieces);
        return Pack(pieces, chunkSize, overlap);
    }

    private static void SplitToFit(string text, int level, int chunkSize, List<string> output)
    {
        if (text.Length <= chunkSize)
        {
            output.Add(text);
            return;
        }

        if (level == Boundaries.Length)
        {
            for (var i = 0; i < text.Length; i += chunkSize)
                output.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
            return;
        }

        foreach (var part in Boundaries[level].Split(text))
        {
            if (part.Length > 0) SplitToFit(part, level + 1, chunkSize, output);
        }
    }

    private static List<string> Pack(List<string> pieces, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var length = 0;
        var hasNewContent = false; // don't emit a chunk that is only carried-over overlap

        foreach (var piece in pieces)
        {
            if (length + piece.Length > chunkSize && current.Count > 0)
            {
                if (hasNewContent) Emit(current, chunks);

                // Carry over whole trailing pieces that fit in the overlap budget...
                var carried = new List<string>();
                var carriedLength = 0;
                for (var i = current.Count - 1; i >= 0 && carriedLength + current[i].Length <= overlap; i--)
                {
                    carried.Insert(0, current[i]);
                    carriedLength += current[i].Length;
                }

                // Last piece too long to carry whole (e.g. a long sentence): carry its trailing words instead.
                if (carried.Count == 0 && overlap > 0 && WordTail(current[^1], overlap) is { Length: > 0 } tail)
                {
                    carried.Add(tail);
                    carriedLength = tail.Length;
                }

                // ...but never so much that the next piece no longer fits.
                while (carried.Count > 0 && carriedLength + piece.Length > chunkSize)
                {
                    carriedLength -= carried[0].Length;
                    carried.RemoveAt(0);
                }

                current = carried;
                length = carriedLength;
                hasNewContent = false;
            }

            current.Add(piece);
            length += piece.Length;
            if (!string.IsNullOrWhiteSpace(piece)) hasNewContent = true;
        }

        if (hasNewContent) Emit(current, chunks);
        return chunks;
    }

    /// <summary>The longest suffix of <paramref name="piece"/> that is at most <paramref name="max"/> chars and starts at a word.</summary>
    private static string WordTail(string piece, int max)
    {
        if (piece.Length <= max) return piece;
        var start = piece.Length - max;
        while (start < piece.Length && !char.IsWhiteSpace(piece[start - 1])) start++;
        return start < piece.Length ? piece[start..] : string.Empty;
    }

    private static void Emit(List<string> pieces, List<string> chunks)
    {
        var sb = new StringBuilder();
        foreach (var p in pieces) sb.Append(p);
        var chunk = sb.ToString().Trim();
        if (chunk.Length > 0) chunks.Add(chunk);
    }
}
