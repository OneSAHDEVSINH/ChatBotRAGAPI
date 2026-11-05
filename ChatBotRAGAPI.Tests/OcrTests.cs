using System.Text.Json;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ChatBotRAGAPI.Tests;

/// <summary>Returns fixed text for each image and records what it was sent.</summary>
internal sealed class FakeOcr(params string[] answers) : IImageTextReader
{
    public List<byte[]> Images { get; } = [];

    public Task<string> ReadTextAsync(byte[] image, CancellationToken ct)
    {
        Images.Add(image);
        return Task.FromResult(answers.Length == 0 ? "" : answers[(Images.Count - 1) % answers.Length]);
    }
}

public sealed class PdfOcrTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("pdfocr-").FullName;

    // A 1x1 PNG, standing in for a scanned page image.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A PDF whose pages are either text lines or (null) one full-page image with no text layer, like a scan.</summary>
    private async Task<string> PdfAsync(params string[]?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            if (lines is null) page.AddPng(Png, new PdfRectangle(0, 0, 595, 842));
            else for (var i = 0; i < lines.Length; i++) page.AddText(lines[i], 12, new PdfPoint(50, 750 - i * 20), font);
        }
        var path = Path.Combine(_dir, $"{Guid.NewGuid()}.pdf");
        await File.WriteAllBytesAsync(path, builder.Build(), Ct);
        return path;
    }

    [Fact]
    public async Task A_scanned_page_is_read_by_ocr()
    {
        var ocr = new FakeOcr("Invoice 0042\nTotal: 380.00");
        var text = await new FileTextExtractor(ocr).ExtractAsync(await PdfAsync([null]), Ct);

        Assert.Equal("Invoice 0042\nTotal: 380.00", text.Trim());
        var sent = Assert.Single(ocr.Images);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], sent[..4]); // sent as an image the model can decode
    }

    [Fact]
    public async Task Pages_with_a_text_layer_never_go_to_ocr()
    {
        var ocr = new FakeOcr("Scanned appendix");
        var text = await new FileTextExtractor(ocr).ExtractAsync(await PdfAsync(["Refund policy", "Full refund within 30 days."], null), Ct);

        Assert.Single(ocr.Images); // only the scanned second page
        Assert.Equal("Refund policy\nFull refund within 30 days.\n\nScanned appendix", text.Trim().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Without_ocr_a_scan_is_refused_with_how_to_enable_it()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new FileTextExtractor().ExtractAsync(await PdfAsync([null]), Ct));
        Assert.Contains("Rag:OcrModel", ex.Message);
    }

    [Fact]
    public async Task Ocr_that_finds_nothing_is_reported()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await new FileTextExtractor(new FakeOcr()).ExtractAsync(await PdfAsync([null]), Ct));
        Assert.Contains("OCR found no text", ex.Message);
    }

    [Fact]
    public async Task Ocr_is_wired_only_when_a_vision_model_is_configured()
    {
        static RagApiFactory App(string ocrModel) => new(settings: new Dictionary<string, string>
        {
            ["Rag:UseLocalAiFallback"] = "false", // OCR goes through Ollama
            ["Rag:OcrModel"] = ocrModel,
        });

        using (var on = App("qwen2.5vl:3b")) Assert.True(on.Services.GetRequiredService<FileTextExtractor>().OcrEnabled);
        using (var off = App("")) Assert.False(off.Services.GetRequiredService<FileTextExtractor>().OcrEnabled);
        await Task.CompletedTask;
    }
}

public class OllamaOcrTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_image_to_the_vision_model_and_returns_clean_text()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json("""{ "response": "<think>reading</think>\n Invoice 0042 \n" }"""));
        var client = new OllamaClient(new HttpClient(handler), Options.Create(new RagOptions
        {
            OllamaBaseUrl = "http://ollama.test:11434", OcrModel = "vision-model", OcrMaxTokens = 1500, DisableThinking = true,
        }));

        var text = await client.ReadTextAsync([1, 2, 3], Ct);

        Assert.Equal("Invoice 0042", text);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("/api/generate", request.RequestUri!.AbsolutePath);
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("vision-model", root.GetProperty("model").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), root.GetProperty("images")[0].GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal(1500, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Contains("exactly as written", root.GetProperty("prompt").GetString());
    }
}
