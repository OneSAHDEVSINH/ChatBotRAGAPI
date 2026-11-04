namespace ChatBotRAGAPI.Services;

public sealed class TextChunker
{
    public IReadOnlyList<string> Chunk(string input, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        if (chunkSize <= overlap) throw new ArgumentException("chunkSize must be > overlap");

        var chunks = new List<string>();
        var step = chunkSize - overlap;
        for (var i = 0; i < input.Length; i += step)
        {
            var len = Math.Min(chunkSize, input.Length - i);
            chunks.Add(input.Substring(i, len));
            if (i + len >= input.Length) break;
        }

        return chunks;
    }
}