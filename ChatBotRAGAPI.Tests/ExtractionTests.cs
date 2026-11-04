using System.Net;
using ChatBotRAGAPI.Services;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace ChatBotRAGAPI.Tests;

public sealed class FileTextExtractorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("ragtests-").FullName;
    private readonly FileTextExtractor _extractor = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Docx_gives_one_line_per_paragraph_and_joins_split_runs()
    {
        var path = Path.Combine(_dir, "doc.docx");
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            doc.AddMainDocumentPart().Document = new Document(new Body(
                new Paragraph(new Run(new Text("Hel")), new Run(new Text("lo world"))),
                new Paragraph(new Run(new Text("Line A"), new Break(), new Text("Line B"))),
                new Paragraph(new Run(new Text("Key"), new TabChar(), new Text("Value")))));
        }

        Assert.Equal("Hello world\nLine A\nLine B\nKey\tValue\n", await _extractor.ExtractAsync(path, Ct));
    }

    [Fact]
    public async Task Xlsx_gives_one_pipe_separated_line_per_row()
    {
        var path = Path.Combine(_dir, "book.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "Qty";
            ws.Cell(2, 1).Value = "Widget";
            ws.Cell(2, 2).Value = 3;
            wb.SaveAs(path);
        }

        var lines = (await _extractor.ExtractAsync(path, Ct)).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["Name | Qty", "Widget | 3"], lines);
    }

    [Fact]
    public async Task Unsupported_extension_throws()
    {
        var path = Path.Combine(_dir, "x.pdf");
        await File.WriteAllTextAsync(path, "x", Ct);
        await Assert.ThrowsAsync<NotSupportedException>(() => _extractor.ExtractAsync(path, Ct));
    }

    [Theory]
    [InlineData("company_profile.txt")]
    [InlineData("service_catalog.csv")]
    [InlineData("client_success_story.docx")]
    [InlineData("quarterly_kpi.xlsx")]
    public async Task Sample_files_extract_to_multiline_text(string file)
    {
        var text = await _extractor.ExtractAsync(Path.Combine(AppContext.BaseDirectory, "testdata", file), Ct);
        Assert.True(text.Trim().Length > 50, $"too little text from {file}");
        Assert.Contains('\n', text.Trim());
    }
}

public class WebScraperServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebScraperService Scraper(HttpResponseMessage response) =>
        new(new HttpClient(new StubHttpHandler((_, _) => response)));

    [Fact]
    public async Task Keeps_block_structure_and_drops_boilerplate()
    {
        const string html = """
            <html><head><title> Acme   Docs </title><style>p{}</style></head>
            <body>
              <nav>Home | About</nav>
              <main>
                <h2>Pricing</h2><p>Plans start at $10.</p><p>Line one<br>Line two</p>
                <ul><li>Basic</li><li>Pro</li></ul>
                <script>var secret = 1;</script>
              </main>
              <footer>Copyright</footer>
            </body></html>
            """;

        var text = await Scraper(StubHttpHandler.Html(html)).ScrapeTextAsync("https://acme.test/", Ct);

        Assert.Equal("Acme Docs\n\nPricing\n\nPlans start at $10.\n\nLine one\nLine two\n\nBasic\nPro", text);
    }

    [Fact]
    public async Task Forbidden_gives_a_helpful_error()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Scraper(new HttpResponseMessage(HttpStatusCode.Forbidden)).ScrapeTextAsync("https://acme.test/", Ct));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Other_failures_throw_http_errors() =>
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Scraper(new HttpResponseMessage(HttpStatusCode.InternalServerError)).ScrapeTextAsync("https://acme.test/", Ct));
}
