namespace ChatBotRAGAPI.Services;

/// <summary>The transcription instruction for scanned pages, shared by every OCR provider.</summary>
public static class OcrPrompt
{
    public const string Text = """
        Transcribe all the text in this scanned page exactly as written, in reading order.
        Keep line breaks, and separate paragraphs with a blank line. Write tables row by row with " | " between cells.
        Output only the page's text: no commentary, no description of the image, no Markdown.
        If the page contains no text, output nothing.
        """;
}
