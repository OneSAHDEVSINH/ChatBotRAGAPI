namespace ChatBotRAGAPI.Services;

/// <summary>
/// A document's chunk embeddings on disk (<c>{docId}.vec</c> beside its <c>.txt</c>), so unchanged chunks are
/// never embedded twice: not when the in-memory store is rebuilt at startup, and not when a document or a
/// crawled page is ingested again with the same text. Keyed by chunk id, which is derived from the source and
/// the chunk's text, and tagged with the embedding model, so a different model or size reads as empty.
/// Binary, little-endian: magic, version, model id, dimensions, count, then (16-byte id, floats) per chunk.
/// </summary>
public static class VectorFile
{
    private const int Magic = 0x43455652; // "RVEC"
    private const int Version = 1;

    /// <summary>The cached vectors, or an empty map when the file is missing, unreadable or for another model.</summary>
    public static Dictionary<Guid, float[]> Read(string path, string modelId, ILogger? logger = null)
    {
        var vectors = new Dictionary<Guid, float[]>();
        if (!File.Exists(path)) return vectors;

        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) return vectors;
            if (reader.ReadString() != modelId) return vectors; // another model's vector space

            var dimensions = reader.ReadInt32();
            var count = reader.ReadInt32();
            if (dimensions <= 0 || count < 0) return vectors;

            var bytes = new byte[dimensions * sizeof(float)];
            for (var i = 0; i < count; i++)
            {
                var id = new Guid(reader.ReadBytes(16));
                reader.BaseStream.ReadExactly(bytes);
                var vector = new float[dimensions];
                Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
                vectors[id] = vector;
            }
            return vectors;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            // A cache, not a source of truth: a damaged file only costs re-embedding, and is rewritten after.
            logger?.LogWarning(ex, "Ignoring unreadable embedding cache {Path}", path);
            return [];
        }
    }

    /// <summary>Writes the vectors atomically (temp file, then rename), replacing any previous cache.</summary>
    public static void Write(string path, string modelId, IReadOnlyDictionary<Guid, float[]> vectors)
    {
        var dimensions = vectors.Count == 0 ? 0 : vectors.Values.First().Length;
        if (vectors.Values.Any(v => v.Length != dimensions))
            throw new InvalidOperationException("All cached vectors of a document must have the same length.");

        var temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp)))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(modelId);
            writer.Write(dimensions);
            writer.Write(vectors.Count);

            var bytes = new byte[dimensions * sizeof(float)];
            foreach (var (id, vector) in vectors)
            {
                writer.Write(id.ToByteArray());
                Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                writer.Write(bytes);
            }
        }
        File.Move(temp, path, overwrite: true);
    }
}
