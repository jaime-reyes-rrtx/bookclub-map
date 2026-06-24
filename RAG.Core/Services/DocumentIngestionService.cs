using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RAG.Core.Data;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class DocumentIngestionService(
    RagDbContext dbContext,
    IObjectStorage storage,
    ITextExtractor extractor,
    ITextChunker chunker,
    ILiteraryArtifactGenerator literaryArtifacts,
    IEmbeddingProvider embeddings,
    IVectorStore vectorStore,
    ILogger<DocumentIngestionService> logger) : IDocumentIngestionService
{
    public async Task<int> IngestPendingDocumentsAsync(CancellationToken cancellationToken)
    {
        var staleProcessingCutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        var pendingDocuments = await dbContext.Documents
            .Where(document => document.Status == DocumentStatus.Pending)
            .ToListAsync(cancellationToken);
        var staleProcessingDocuments = (await dbContext.Documents
                .Where(document => document.Status == DocumentStatus.Processing)
                .ToListAsync(cancellationToken))
            .Where(document => document.UpdatedAtUtc < staleProcessingCutoff);

        var documents = pendingDocuments
            .Concat(staleProcessingDocuments)
            .OrderBy(document => document.CreatedAtUtc)
            .Take(5)
            .ToList();

        foreach (var document in documents)
        {
            await IngestDocumentAsync(document.Id, cancellationToken);
        }

        return documents.Count;
    }

    public async Task IngestDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var staleProcessingCutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        var document = await dbContext.Documents.SingleOrDefaultAsync(x => x.Id == documentId, cancellationToken);
        if (document is null || document.Status == DocumentStatus.Indexed)
        {
            return;
        }

        if (document.Status == DocumentStatus.Processing && document.UpdatedAtUtc >= staleProcessingCutoff)
        {
            return;
        }

        document.Status = DocumentStatus.Processing;
        document.ErrorMessage = null;
        document.ChunkCount = 0;
        document.ProcessedChunks = 0;
        document.TotalChunks = 0;
        await UpdateProgressAsync(document, "Preparing storage", 2, cancellationToken);

        try
        {
            await storage.EnsureReadyAsync(cancellationToken);
            await vectorStore.EnsureCollectionAsync(cancellationToken);

            await UpdateProgressAsync(document, "Extracting text", 8, cancellationToken);
            await using var original = await storage.OpenReadAsync(document.ObjectKey, cancellationToken);
            var extracted = await extractor.ExtractAsync(original, document.ContentType, document.FileName, cancellationToken);

            await UpdateProgressAsync(document, "Chunking text", 18, cancellationToken);
            var sourceChunks = chunker.Chunk(document.Id, document.FileName, document.ObjectKey, extracted);

            await UpdateProgressAsync(document, "Building book club profile", 25, cancellationToken);
            var artifactChunks = await literaryArtifacts.GenerateArtifactsAsync(
                document.Id,
                document.FileName,
                document.ObjectKey,
                extracted,
                sourceChunks,
                cancellationToken);
            var chunks = artifactChunks.Concat(sourceChunks).ToList();
            var embedded = new List<EmbeddedChunk>(chunks.Count);

            document.TotalChunks = chunks.Count;
            await UpdateProgressAsync(document, "Resetting existing index", 30, cancellationToken);
            await vectorStore.DeleteDocumentAsync(document.Id, cancellationToken);

            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var embedding = await embeddings.GenerateEmbeddingAsync(chunk.Text, cancellationToken);
                embedded.Add(new EmbeddedChunk(chunk, embedding));
                document.ProcessedChunks = embedded.Count;

                if (ShouldSaveEmbeddingProgress(embedded.Count, chunks.Count))
                {
                    var percent = CalculateEmbeddingPercent(embedded.Count, chunks.Count);
                    await UpdateProgressAsync(document, "Generating embeddings", percent, cancellationToken);
                }
            }

            await UpdateProgressAsync(document, "Writing vector index", 92, cancellationToken);
            await vectorStore.UpsertChunksAsync(embedded, cancellationToken);

            document.Status = DocumentStatus.Indexed;
            document.ChunkCount = embedded.Count;
            document.ProcessedChunks = embedded.Count;
            document.TotalChunks = embedded.Count;
            await UpdateProgressAsync(document, "Ready", 100, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ingest document {DocumentId}", documentId);
            document.Status = DocumentStatus.Failed;
            document.ErrorMessage = ex.Message;
            document.ProgressStage = "Failed";
            document.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
    }

    private async Task UpdateProgressAsync(
        DocumentRecord document,
        string stage,
        int percent,
        CancellationToken cancellationToken)
    {
        document.ProgressStage = stage;
        document.ProgressPercent = Math.Clamp(percent, 0, 100);
        document.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool ShouldSaveEmbeddingProgress(int processed, int total)
    {
        if (total <= 0)
        {
            return true;
        }

        return processed == 1 ||
               processed == total ||
               processed % 5 == 0 ||
               processed % Math.Max(1, total / 20) == 0;
    }

    private static int CalculateEmbeddingPercent(int processed, int total)
    {
        if (total <= 0)
        {
            return 88;
        }

        var embeddingProgress = (double)processed / total;
        return 35 + (int)Math.Round(embeddingProgress * 52);
    }
}
