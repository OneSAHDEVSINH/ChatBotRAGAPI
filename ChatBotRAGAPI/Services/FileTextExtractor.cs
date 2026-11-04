using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace ChatBotRAGAPI.Services;

public sealed class FileTextExtractor
{
    public async Task<string> ExtractAsync(string filePath, CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".txt" => await File.ReadAllTextAsync(filePath, ct),
            ".csv" => await File.ReadAllTextAsync(filePath, ct),
            ".docx" => ExtractDocx(filePath),
            ".xlsx" => ExtractXlsx(filePath),
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