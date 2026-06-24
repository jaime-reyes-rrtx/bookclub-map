using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class ApproximateTokenEstimator : ITokenEstimator
{
    public IReadOnlyList<string> EstimateTokens(string text)
    {
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public sealed class TokenTextChunker(IOptions<RagOptions> options, ITokenEstimator? tokenEstimator = null) : ITextChunker
{
    private readonly IngestionOptions _options = options.Value.Ingestion;
    private readonly ITokenEstimator _tokenEstimator = tokenEstimator ?? new ApproximateTokenEstimator();

    public IReadOnlyList<TextChunk> Chunk(Guid documentId, string fileName, string objectKey, ExtractedDocument document)
    {
        var tokens = document.Pages
            .SelectMany(page => _tokenEstimator.EstimateTokens(page.Text).Select(token => new TokenWithPage(token, page.PageNumber)))
            .ToList();

        if (tokens.Count == 0)
        {
            return [];
        }

        var chunkSize = Math.Max(50, _options.ChunkTokenCount);
        var overlap = Math.Clamp(_options.ChunkOverlapTokens, 0, chunkSize / 2);
        var step = chunkSize - overlap;
        var chunks = new List<TextChunk>();

        for (var start = 0; start < tokens.Count; start += step)
        {
            var chunkTokens = tokens.Skip(start).Take(chunkSize).ToList();
            if (chunkTokens.Count == 0)
            {
                break;
            }

            var text = string.Join(' ', chunkTokens.Select(x => x.Text));
            chunks.Add(new TextChunk(
                Guid.NewGuid(),
                documentId,
                fileName,
                objectKey,
                chunks.Count,
                chunkTokens.First().PageNumber,
                text));

            if (start + chunkSize >= tokens.Count)
            {
                break;
            }
        }

        return chunks;
    }

    private sealed record TokenWithPage(string Text, int? PageNumber);
}
