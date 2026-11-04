using System.Text.RegularExpressions;
using ChatBotRAGAPI.Services;

namespace ChatBotRAGAPI.Tests;

public class TextChunkerTests
{
    private readonly TextChunker _chunker = new();

    private static string Sentences(int count) =>
        string.Join(" ", Enumerable.Range(1, count).Select(i => $"Sentence number {i} talks about topic {i * 7}."));

    private static HashSet<string> Words(string s) => Regex.Matches(s, @"\S+").Select(m => m.Value).ToHashSet();

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Blank_input_gives_no_chunks(string input) => Assert.Empty(_chunker.Chunk(input, 100, 10));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(100, -1)]
    public void Invalid_sizes_throw(int size, int overlap) =>
        Assert.ThrowsAny<ArgumentException>(() => _chunker.Chunk("text", size, overlap));

    [Fact]
    public void Short_text_is_one_trimmed_chunk() =>
        Assert.Equal(["Hello world."], _chunker.Chunk("  Hello world.  \n", 100, 10));

    [Fact]
    public void Chunks_never_exceed_chunk_size()
    {
        var chunks = _chunker.Chunk(Sentences(80), 200, 60);
        Assert.True(chunks.Count > 5);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 200));
    }

    [Fact]
    public void Splits_on_paragraph_boundaries_when_they_fit()
    {
        var p1 = "First paragraph about apples and their many varieties.";
        var p2 = "Second paragraph about oranges and citrus farming.";
        var chunks = _chunker.Chunk($"{p1}\n\n{p2}", 70, 0);
        Assert.Equal([p1, p2], chunks);
    }

    [Fact]
    public void Never_cuts_words_in_half()
    {
        var input = Sentences(60);
        var sourceWords = Words(input);
        foreach (var chunk in _chunker.Chunk(input, 150, 40))
            Assert.Subset(sourceWords, Words(chunk));
    }

    [Fact]
    public void Every_word_is_covered()
    {
        var input = Sentences(60);
        var covered = _chunker.Chunk(input, 150, 40).SelectMany(Words).ToHashSet();
        Assert.Subset(covered, Words(input));
    }

    [Fact]
    public void Consecutive_chunks_overlap()
    {
        var chunks = _chunker.Chunk(Sentences(60), 200, 60);
        for (var i = 1; i < chunks.Count; i++)
        {
            var head = chunks[i][..Math.Min(20, chunks[i].Length)];
            Assert.Contains(head, chunks[i - 1]);
        }
    }

    [Fact]
    public void Overlap_carries_trailing_words_of_a_long_sentence()
    {
        // One sentence longer than chunkSize: no whole piece fits the overlap budget, so trailing words are carried.
        var sentence = string.Join(" ", Enumerable.Range(1, 60).Select(i => $"word{i}"));
        var chunks = _chunker.Chunk(sentence, 100, 30);
        Assert.True(chunks.Count > 1);
        for (var i = 1; i < chunks.Count; i++)
            Assert.Contains(chunks[i].Split(' ')[0], chunks[i - 1].Split(' '));
    }

    [Fact]
    public void Single_overlong_word_is_hard_cut()
    {
        var word = new string('x', 250);
        var chunks = _chunker.Chunk(word, 100, 0);
        Assert.Equal([100, 100, 50], chunks.Select(c => c.Length));
        Assert.Equal(word, string.Concat(chunks));
    }

    [Fact]
    public void Line_endings_are_normalized()
    {
        var chunks = _chunker.Chunk("line one\r\nline two\rline three", 100, 0);
        Assert.Equal(["line one\nline two\nline three"], chunks);
    }

    [Fact]
    public void Does_not_emit_overlap_only_chunk()
    {
        var chunks = _chunker.Chunk(Sentences(10), 120, 100);
        Assert.Equal(chunks.Count, chunks.Distinct().Count());
        for (var i = 1; i < chunks.Count; i++)
            Assert.False(chunks[i - 1].EndsWith(chunks[i]), $"chunk {i} is only overlap");
    }
}
