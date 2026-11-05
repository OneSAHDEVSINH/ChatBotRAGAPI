namespace ChatBotRAGAPI.Services;

/// <summary>Reads the text in an image (OCR). Used for PDF pages that have no text layer, i.e. scans.</summary>
public interface IImageTextReader
{
    /// <param name="image">PNG or JPEG bytes.</param>
    /// <returns>The transcribed text, or an empty string when the image holds none.</returns>
    Task<string> ReadTextAsync(byte[] image, CancellationToken ct);
}
