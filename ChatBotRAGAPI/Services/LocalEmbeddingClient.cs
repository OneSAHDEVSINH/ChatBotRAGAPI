using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

public sealed class LocalEmbeddingClient : IEmbeddingClient
{
    private static readonly Regex TokenRegex = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly int _dimensions;

    public LocalEmbeddingClient(IOptions<RagOptions> options)
    {
        _dimensions = Math.Max(64, options.Value.EmbeddingDimensions);
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var vec = new float[_dimensions];
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult(vec);

        foreach (Match match in TokenRegex.Matches(text))
        {
            var token = match.Value.ToLowerInvariant();
            var idx = StableHash(token) % _dimensions;
            vec[idx] += 1f;
        }

        Normalize(vec);
        return Task.FromResult(vec);
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