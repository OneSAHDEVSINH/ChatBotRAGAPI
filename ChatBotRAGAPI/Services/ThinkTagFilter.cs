using System.Text;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Removes inline &lt;think&gt;…&lt;/think&gt; blocks from a token stream, where a tag can be split across pieces
/// (e.g. "&lt;thi" + "nk&gt;"). Leading whitespace of the visible answer is dropped, matching the non-streaming Trim().
/// </summary>
public sealed class ThinkTagFilter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private readonly StringBuilder _pending = new();
    private bool _inThink;
    private bool _started;

    public string Push(string piece)
    {
        _pending.Append(piece);
        var output = new StringBuilder();

        while (true)
        {
            var text = _pending.ToString();
            var tag = _inThink ? Close : Open;
            var index = text.IndexOf(tag, StringComparison.Ordinal);

            if (index >= 0)
            {
                if (!_inThink) output.Append(text, 0, index);
                _pending.Remove(0, index + tag.Length);
                _inThink = !_inThink;
                continue;
            }

            // Hold back a trailing partial tag ("<th") until the next piece shows whether it is one.
            var keep = PartialTagSuffix(text, tag);
            if (!_inThink) output.Append(text, 0, text.Length - keep);
            _pending.Remove(0, text.Length - keep);
            return Emit(output.ToString());
        }
    }

    /// <summary>Call at end of stream: returns held-back text that turned out not to be a tag.</summary>
    public string Flush()
    {
        var rest = _inThink ? string.Empty : _pending.ToString();
        _pending.Clear();
        return Emit(rest);
    }

    private string Emit(string text)
    {
        if (!_started)
        {
            text = text.TrimStart();
            _started = text.Length > 0;
        }
        return text;
    }

    private static int PartialTagSuffix(string text, string tag)
    {
        for (var n = Math.Min(tag.Length - 1, text.Length); n > 0; n--)
        {
            if (tag.AsSpan().StartsWith(text.AsSpan(text.Length - n), StringComparison.Ordinal)) return n;
        }
        return 0;
    }
}
