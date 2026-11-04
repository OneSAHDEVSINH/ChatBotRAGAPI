using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public sealed class DocumentRegistryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("registry-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DocumentRegistry Create() =>
        new(Options.Create(new RagOptions { DataDirectory = _dir }), NullLogger<DocumentRegistry>.Instance);

    [Fact]
    public async Task Entries_and_texts_survive_a_reload()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var registry = Create();
        await registry.SaveTextAsync("https://example.com/a?b=c", "page text", Ct);
        registry.Set("https://example.com/a?b=c", "url", ids);

        var reloaded = Create();
        var entry = Assert.Single(reloaded.List());
        Assert.Equal("https://example.com/a?b=c", entry.Source);
        Assert.Equal("url", entry.SourceType);
        Assert.Equal(ids, entry.ChunkIds);
        Assert.Equal("page text", await File.ReadAllTextAsync(reloaded.TextPath(entry.Source), Ct));
    }

    [Fact]
    public async Task Remove_deletes_entry_and_text()
    {
        var registry = Create();
        await registry.SaveTextAsync("a.txt", "x", Ct);
        registry.Set("a.txt", "file", []);

        Assert.True(registry.Remove("a.txt"));
        Assert.False(registry.Remove("a.txt"));
        Assert.Null(Create().Get("a.txt"));
        Assert.False(File.Exists(registry.TextPath("a.txt")));
    }

    [Fact]
    public void Text_paths_are_file_name_safe_and_distinct()
    {
        var registry = Create();
        var a = registry.TextPath("https://example.com/a/b?c=<d>");
        var b = registry.TextPath("https://example.com/a/b?c=<e>");
        Assert.NotEqual(a, b);
        Assert.Equal(Path.Combine(_dir, "documents"), Path.GetDirectoryName(a));
    }

    [Fact]
    public void Corrupt_index_is_set_aside_instead_of_overwritten()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "documents"));
        File.WriteAllText(Path.Combine(_dir, "documents", "index.json"), "{ not json");

        var registry = Create();

        Assert.Empty(registry.List());
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "documents"), "index.json.corrupt-*"));
    }

    [Fact]
    public async Task Lock_is_exclusive()
    {
        var registry = Create();
        var first = await registry.LockAsync(Ct);
        var second = registry.LockAsync(Ct);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
    }
}
