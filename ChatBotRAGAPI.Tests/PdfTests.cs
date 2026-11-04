using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ChatBotRAGAPI.Tests;

internal static class TestPdf
{
    /// <summary>Builds a PDF where each page is a list of text lines (an empty page has no text layer, like a scan).</summary>
    public static byte[] Create(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++)
                page.AddText(lines[i], 12, new PdfPoint(50, 750 - i * 20), font);
        }
        return builder.Build();
    }
}

public sealed class PdfExtractionTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("pdftests-").FullName;
    private readonly FileTextExtractor _extractor = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> WriteAsync(byte[] bytes)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid()}.pdf");
        await File.WriteAllBytesAsync(path, bytes, Ct);
        return path;
    }

    [Fact]
    public async Task Keeps_lines_and_separates_pages()
    {
        var path = await WriteAsync(TestPdf.Create(
            ["Refund policy", "Full refund within 30 days."],
            ["Support hours", "Monday to Friday."]));

        var text = await _extractor.ExtractAsync(path, Ct);

        Assert.Equal("Refund policy\nFull refund within 30 days.\n\nSupport hours\nMonday to Friday.\n\n",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Pages_without_text_are_skipped()
    {
        var path = await WriteAsync(TestPdf.Create([], ["Only page with text."]));
        Assert.Equal("Only page with text.", (await _extractor.ExtractAsync(path, Ct)).Trim());
    }

    [Fact]
    public async Task Pdf_without_any_text_fails_with_a_clear_message()
    {
        var path = await WriteAsync(TestPdf.Create([], []));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _extractor.ExtractAsync(path, Ct));
        Assert.Contains("OCR is not supported", ex.Message);
    }

    [Fact]
    public async Task Corrupt_pdf_throws()
    {
        var path = await WriteAsync("not really a pdf"u8.ToArray());
        await Assert.ThrowsAnyAsync<Exception>(() => _extractor.ExtractAsync(path, Ct));
    }

    [Fact]
    public void Pdf_is_a_supported_upload_type() =>
        Assert.Contains(".PDF", FileTextExtractor.SupportedExtensions);
}

public sealed class PdfApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly RagApiFactory _factory = new();
    private readonly HttpClient _client;

    public PdfApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<JobStatusResponse> UploadAndWaitAsync(string fileName, byte[] bytes)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", fileName } };
        var response = await _client.PostAsync("/api/ingestion/upload", form, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;

        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{jobId}", Ct);
            if (status!.Status is "Completed" or "Failed") return status;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Uploaded_pdf_is_ingested_and_cited()
    {
        var status = await UploadAndWaitAsync("Policies.PDF",
            TestPdf.Create(["Refund policy", "Customers may request a full refund within 30 days."]));
        Assert.Equal("Completed", status.Status);

        var response = await _client.PostAsJsonAsync("/api/chat/ask", new AskRequest("What is the refund policy?"), Ct);
        var result = (await response.Content.ReadFromJsonAsync<AskResponse>(Ct))!;

        Assert.Contains("30 days", result.Answer);
        Assert.Equal("Policies.PDF", result.Sources[0].Source);
    }

    [Fact]
    public async Task Scanned_pdf_job_fails_with_reason()
    {
        var status = await UploadAndWaitAsync("scan.pdf", TestPdf.Create([]));
        Assert.Equal("Failed", status.Status);
        Assert.Contains("no extractable text", status.Message);
    }
}
