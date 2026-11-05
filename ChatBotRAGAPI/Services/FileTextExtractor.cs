using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ChatBotRAGAPI.Services;

public sealed class FileTextExtractor
{
    private readonly IImageTextReader? _ocr;
    private readonly ILogger<FileTextExtractor>? _logger;

    /// <param name="ocr">Reads scanned pages; null (no Rag:OcrModel) means PDFs without a text layer are refused.</param>
    public FileTextExtractor(IImageTextReader? ocr = null, ILogger<FileTextExtractor>? logger = null)
    {
        _ocr = ocr;
        _logger = logger;
    }

    public bool OcrEnabled => _ocr is not null;

    /// <summary>The single list of accepted upload types (the controller validates against it).</summary>
    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".docx", ".xlsx", ".pdf" };

    public async Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".txt" => await File.ReadAllTextAsync(filePath, ct),
            ".csv" => await File.ReadAllTextAsync(filePath, ct),
            ".docx" => ExtractDocx(filePath),
            ".xlsx" => ExtractXlsx(filePath),
            ".pdf" => await ExtractPdfAsync(filePath, ct),
            _ => throw new NotSupportedException($"Unsupported file type: {ext}")
        };
    }

    /// <summary>
    /// One line per paragraph, with runs concatenated (Word often splits a word across runs, so joining
    /// with spaces would break words) and line breaks/tabs kept, so the chunker can split on structure.
    /// </summary>
    private static string ExtractDocx(string filePath)
    {
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            foreach (var element in paragraph.Descendants())
            {
                switch (element)
                {
                    case Text t: sb.Append(t.Text); break;
                    case Break: sb.Append('\n'); break;
                    case TabChar: sb.Append('\t'); break;
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Text in content-stream order with line breaks kept and likely paragraph breaks as blank lines, so the
    /// chunker can split on structure; pages are separated by a blank line. A page with no text layer (a scan) is
    /// read by OCR when it is enabled, from the images on that page; pages that do have text never go to OCR.
    /// A PDF that yields no text at all fails with the reason, instead of ingesting nothing.
    /// </summary>
    private async Task<string> ExtractPdfAsync(string filePath, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(filePath);
        var options = new ContentOrderTextExtractor.Options
        {
            SeparateParagraphsWithDoubleNewline = true,
            ReplaceWhitespaceWithSpace = true,
        };

        var sb = new StringBuilder();
        int scannedPages = 0, unreadableImages = 0;
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page, options).Trim();
            if (text.Length == 0 && _ocr is not null)
            {
                scannedPages++;
                var pageText = new StringBuilder();
                foreach (var image in page.GetImages())
                {
                    if (OcrBytes(image) is not { } bytes) { unreadableImages++; continue; }
                    var read = (await _ocr.ReadTextAsync(bytes, ct)).Trim();
                    if (read.Length > 0) pageText.Append(read).Append("\n\n");
                }
                text = pageText.ToString().Trim();
            }
            if (text.Length > 0) sb.Append(text).Append("\n\n");
        }

        if (scannedPages > 0)
            _logger?.LogInformation("{File}: {Pages} page(s) without a text layer read by OCR ({Unreadable} image(s) in an unsupported format)",
                Path.GetFileName(filePath), scannedPages, unreadableImages);

        if (sb.Length == 0)
            throw new InvalidOperationException(_ocr is null
                ? "The PDF contains no extractable text (it may be scanned images). OCR is off: set Rag:OcrModel to an Ollama vision model to read scans."
                : unreadableImages > 0
                    ? $"The PDF has no text layer, and OCR found no text: {unreadableImages} image(s) are in a format it cannot read (e.g. JBIG2, CCITT or JPEG 2000)."
                    : "The PDF has no text layer, and OCR found no text in its images.");

        return sb.ToString();
    }

    /// <summary>Image bytes a vision model accepts: PNG when PdfPig can convert it, else the raw stream if it is a JPEG.</summary>
    private static byte[]? OcrBytes(UglyToad.PdfPig.Content.IPdfImage image)
    {
        if (image.TryGetPng(out var png)) return png;
        var raw = image.RawBytes.ToArray();
        return raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF ? raw : null; // JPEG signature
    }

    private static string ExtractXlsx(string filePath)
    {
        using var workbook = new XLWorkbook(filePath);
        var sb = new StringBuilder();

        foreach (var ws in workbook.Worksheets)
        {
            var range = ws.RangeUsed();
            if (range is null) continue;

            foreach (var row in range.RowsUsed())
            {
                var cells = row.Cells().Select(c => c.GetValue<string>());
                sb.AppendLine(string.Join(" | ", cells));
            }
        }

        return sb.ToString();
    }
}