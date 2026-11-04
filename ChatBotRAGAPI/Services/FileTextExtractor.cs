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

    private static string ExtractDocx(string filePath)
    {
        using var doc = WordprocessingDocument.Open(filePath, false);
        return string.Join(' ', doc.MainDocumentPart?
            .Document?
            .Descendants<Text>()
            .Select(t => t.Text) ?? []);
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