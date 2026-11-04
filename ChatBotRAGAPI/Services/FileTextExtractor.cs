using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ChatBotRAGAPI.Services;

public sealed class FileTextExtractor
{
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
            ".pdf" => ExtractPdf(filePath, ct),
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
    /// chunker can split on structure; pages are separated by a blank line. Scanned (image-only) PDFs have
    /// no text layer and OCR is not supported, so they fail with an explicit message instead of ingesting nothing.
    /// </summary>
    private static string ExtractPdf(string filePath, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(filePath);
        var options = new ContentOrderTextExtractor.Options
        {
            SeparateParagraphsWithDoubleNewline = true,
            ReplaceWhitespaceWithSpace = true,
        };

        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page, options).Trim();
            if (text.Length > 0) sb.Append(text).Append("\n\n");
        }

        if (sb.Length == 0)
            throw new InvalidOperationException(
                "The PDF contains no extractable text (it may be scanned images; OCR is not supported).");

        return sb.ToString();
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