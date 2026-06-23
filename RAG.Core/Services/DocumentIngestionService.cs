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
        document.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await storage.EnsureReadyAsync(cancellationToken);
            await vectorStore.EnsureCollectionAsync(cancellationToken);

            await using var original = await storage.OpenReadAsync(document.ObjectKey, cancellationToken);
            var extracted = await extractor.ExtractAsync(original, document.ContentType, document.FileName, cancellationToken);
            var sourceChunks = chunker.Chunk(document.Id, document.FileName, document.ObjectKey, extracted);
            var artifactChunks = await literaryArtifacts.GenerateArtifactsAsync(
                document.Id,
                document.FileName,
                document.ObjectKey,
                extracted,
                sourceChunks,
                cancellationToken);
            var chunks = artifactChunks.Concat(sourceChunks).ToList();
            var embedded = new List<EmbeddedChunk>(chunks.Count);

            await vectorStore.DeleteDocumentAsync(document.Id, cancellationToken);

            foreach (var chunk in chunks)
            {
                var embedding = await embeddings.GenerateEmbeddingAsync(chunk.Text, cancellationToken);
                embedded.Add(new EmbeddedChunk(chunk, embedding));
            }

            await vectorStore.UpsertChunksAsync(embedded, cancellationToken);

            document.Status = DocumentStatus.Indexed;
            document.ChunkCount = embedded.Count;
            document.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ingest document {DocumentId}", documentId);
            document.Status = DocumentStatus.Failed;
            document.ErrorMessage = ex.Message;
            document.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
    }
}
