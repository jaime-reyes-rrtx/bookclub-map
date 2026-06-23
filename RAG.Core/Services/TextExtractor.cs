using System.Text;
using RAG.Core.Models;
using UglyToad.PdfPig;

namespace RAG.Core.Services;

public sealed class TextExtractor : ITextExtractor
{
    public async Task<ExtractedDocument> ExtractAsync(
        Stream content,
        string contentType,
        string fileName,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension == ".txt" || contentType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            return new ExtractedDocument([new ExtractedPage(null, text)]);
        }

        if (extension == ".pdf" || contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            using var pdf = PdfDocument.Open(memory);
            var pages = pdf.GetPages()
                .Select(page => new ExtractedPage(page.Number, page.Text))
                .Where(page => !string.IsNullOrWhiteSpace(page.Text))
                .ToList();

            return new ExtractedDocument(pages);
        }

        throw new InvalidOperationException($"Unsupported file type '{fileName}'.");
    }
}
