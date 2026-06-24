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
    IIngestionWorkSource workSource,
    ILogger<DocumentIngestionService> logger) : IDocumentIngestionService
{
    public DocumentIngestionService(
        RagDbContext dbContext,
        IObjectStorage storage,
        ITextExtractor extractor,
        ITextChunker chunker,
        ILiteraryArtifactGenerator literaryArtifacts,
        IEmbeddingProvider embeddings,
        IVectorStore vectorStore,
        ILogger<DocumentIngestionService> logger)
        : this(
            dbContext,
            storage,
            extractor,
            chunker,
            literaryArtifacts,
            embeddings,
            vectorStore,
            new DatabaseIngestionWorkSource(dbContext),
            logger)
    {
    }

    public async Task<int> IngestPendingDocumentsAsync(CancellationToken cancellationToken)
    {
        var documentIds = await workSource.GetNextDocumentIdsAsync(cancellationToken);
        logger.LogInformation("Ingestion work source returned {DocumentCount} document(s).", documentIds.Count);

        foreach (var documentId in documentIds)
        {
            await IngestDocumentAsync(documentId, cancellationToken);
        }

        return documentIds.Count;
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
        logger.LogInformation(
            "Ingestion started for document {DocumentId} ({FileName}).",
            document.Id,
            document.FileName);

        try
        {
            await storage.EnsureReadyAsync(cancellationToken);
            await vectorStore.EnsureCollectionAsync(cancellationToken);

            await UpdateProgressAsync(document, "Extracting text", 8, cancellationToken);
            await using var original = await storage.OpenReadAsync(document.ObjectKey, cancellationToken);
            var extracted = await extractor.ExtractAsync(original, document.ContentType, document.FileName, cancellationToken);
            logger.LogInformation(
                "Extracted {PageCount} page(s) from document {DocumentId}.",
                extracted.Pages.Count,
                document.Id);

            await UpdateProgressAsync(document, "Chunking text", 18, cancellationToken);
            var sourceChunks = chunker.Chunk(document.Id, document.FileName, document.ObjectKey, extracted);
            logger.LogInformation(
                "Created {SourceChunkCount} source chunk(s) for document {DocumentId}.",
                sourceChunks.Count,
                document.Id);

            await UpdateProgressAsync(document, "Building book club profile", 25, cancellationToken);
            var artifactChunks = await literaryArtifacts.GenerateArtifactsAsync(
                document.Id,
                document.FileName,
                document.ObjectKey,
                extracted,
                sourceChunks,
                cancellationToken);
            logger.LogInformation(
                "Created {GeneratedArtifactCount} generated artifact chunk(s) for document {DocumentId}.",
                artifactChunks.Count,
                document.Id);
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
                if (embedded.Count == chunks.Count)
                {
                    logger.LogInformation(
                        "Generated {EmbeddingCount} embedding(s) for document {DocumentId}.",
                        embedded.Count,
                        document.Id);
                }

                if (ShouldSaveEmbeddingProgress(embedded.Count, chunks.Count))
                {
                    var percent = CalculateEmbeddingPercent(embedded.Count, chunks.Count);
                    await UpdateProgressAsync(document, "Generating embeddings", percent, cancellationToken);
                }
            }

            await UpdateProgressAsync(document, "Writing vector index", 92, cancellationToken);
            await vectorStore.UpsertChunksAsync(embedded, cancellationToken);
            logger.LogInformation(
                "Upserted {VectorCount} vector point(s) for document {DocumentId}.",
                embedded.Count,
                document.Id);

            document.Status = DocumentStatus.Indexed;
            document.ChunkCount = embedded.Count;
            document.ProcessedChunks = embedded.Count;
            document.TotalChunks = embedded.Count;
            await UpdateProgressAsync(document, "Ready", 100, cancellationToken);
            logger.LogInformation(
                "Ingestion completed for document {DocumentId} with {ChunkCount} total chunk(s).",
                document.Id,
                document.ChunkCount);
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
