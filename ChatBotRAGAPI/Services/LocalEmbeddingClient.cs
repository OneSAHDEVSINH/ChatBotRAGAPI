using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

public sealed class LocalEmbeddingClient : IEmbeddingClient
{
    private readonly int _dimensions;

    public LocalEmbeddingClient(IOptions<RagOptions> options)
    {
        _dimensions = Math.Max(64, options.Value.EmbeddingDimensions);
    }

    public string ModelId => $"local-hash:{_dimensions}";

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    private float[] Embed(string text)
    {
        var vec = new float[_dimensions];
        if (string.IsNullOrWhiteSpace(text))
            return vec;

        foreach (var token in LocalText.Tokenize(text))
        {
            var idx = StableHash(token) % _dimensions;
            vec[idx] += 1f;
        }

        Normalize(vec);
        return vec;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in s)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return (int)(hash & 0x7fffffff);
        }
    }

    private static void Normalize(float[] v)
    {
        double sum = 0;
        for (var i = 0; i < v.Length; i++) sum += v[i] * v[i];
        if (sum <= 0) return;

        var norm = (float)Math.Sqrt(sum);
        for (var i = 0; i < v.Length; i++) v[i] /= norm;
    }
}