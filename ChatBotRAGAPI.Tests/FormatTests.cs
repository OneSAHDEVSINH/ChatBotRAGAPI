using ChatBotRAGAPI.Services;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace ChatBotRAGAPI.Tests;

/// <summary>The .md, .html/.htm and .pptx uploads.</summary>
public sealed class FormatTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("ragformats-").FullName;
    private readonly FileTextExtractor _extractor = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> Extract(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content, Ct);
        return await _extractor.ExtractAsync(path, Ct);
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".html")]
    [InlineData(".htm")]
    [InlineData(".pptx")]
    public void New_types_are_accepted(string ext) => Assert.Contains(ext, FileTextExtractor.SupportedExtensions);

    // ---------- Markdown ----------

    [Fact]
    public async Task Markdown_keeps_its_structure_and_drops_link_targets_comments_and_fences()
    {
        var text = await Extract("guide.md", """
            # Support plans

            <!-- internal: update prices in Q3 -->
            The **Gold** plan includes [emergency support](https://example.com/sla) 24/7.
            ![Diagram of the escalation path](img/escalation.png)

            - Silver: next business day
            - Gold: 1 hour

            ```bash
            curl https://example.com/status
            ```
            """);

        Assert.Equal("""
            # Support plans


            The **Gold** plan includes emergency support 24/7.
            Diagram of the escalation path

            - Silver: next business day
            - Gold: 1 hour

            curl https://example.com/status

            """.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"));
    }

    // ---------- HTML ----------

    [Fact]
    public async Task Html_gives_the_same_text_as_a_crawled_page()
    {
        var text = await Extract("pricing.html", """
            <!doctype html><html><head><title>Pricing</title><style>p { color: red }</style></head>
            <body>
              <nav><a href="/">Home</a> <a href="/about">About</a></nav>
              <main>
                <h1>Plans</h1><p>Gold costs 900 a month.</p>
                <ul><li>Monitoring</li><li>Bug fixes</li></ul>
              </main>
              <footer>© Acme</footer>
              <script>track()</script>
            </body></html>
            """);

        Assert.Equal("Pricing\n\nPlans\n\nGold costs 900 a month.\n\nMonitoring\nBug fixes", text);
    }

    [Fact]
    public async Task Htm_is_html_too() =>
        Assert.Equal("Hello", await Extract("old.htm", "<html><body><p>Hello</p></body></html>"));

    // ---------- PowerPoint ----------

    private string Pptx(params (string[] Lines, string[]? Notes, bool Hidden)[] slides)
    {
        var path = Path.Combine(_dir, "deck.pptx");
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = doc.AddPresentationPart();
        var ids = new P.SlideIdList();
        presentation.Presentation = new P.Presentation(ids);

        uint id = 256;
        foreach (var (lines, notes, hidden) in slides)
        {
            var slidePart = presentation.AddNewPart<SlidePart>();
            slidePart.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(TextShape(lines))));
            if (hidden) slidePart.Slide.Show = false;
            if (notes is not null)
            {
                var notesPart = slidePart.AddNewPart<NotesSlidePart>();
                // Real notes pages carry the slide number as a text placeholder too.
                notesPart.NotesSlide = new P.NotesSlide(new P.CommonSlideData(new P.ShapeTree(TextShape(notes), TextShape(["7"]))));
            }
            ids.Append(new P.SlideId { Id = id++, RelationshipId = presentation.GetIdOfPart(slidePart) });
        }
        return path;
    }

    private static P.Shape TextShape(string[] paragraphs) =>
        new(new P.TextBody([new A.BodyProperties(), .. paragraphs.Select(text =>
        {
            // Split into two runs like PowerPoint often does, to prove runs are joined without spaces.
            var cut = text.Length / 2;
            return (OpenXmlElement)new A.Paragraph(new A.Run(new A.Text(text[..cut])), new A.Run(new A.Text(text[cut..])));
        })]));

    [Fact]
    public async Task Pptx_gives_each_slide_its_text_and_notes_in_order()
    {
        var path = Pptx(
            (["Support plans", "Silver: next business day", "Gold: 1 hour"], ["Mention the Gold upgrade offer."], false),
            (["Internal only"], null, true),
            (["Contact", "support@example.com"], null, false));

        var text = await _extractor.ExtractAsync(path, Ct);

        Assert.Equal("""
            Slide 1
            Support plans
            Silver: next business day
            Gold: 1 hour
            Notes:
            Mention the Gold upgrade offer.

            Slide 3
            Contact
            support@example.com


            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public async Task An_empty_presentation_gives_no_text()
    {
        var path = Pptx(([], null, false));
        Assert.Equal("", await _extractor.ExtractAsync(path, Ct));
    }
}
