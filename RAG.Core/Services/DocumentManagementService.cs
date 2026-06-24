using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RAG.Core.Data;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class DocumentManagementService(
    RagDbContext dbContext,
    IObjectStorage storage,
    IVectorStore vectorStore,
    ILogger<DocumentManagementService> logger) : IDocumentManagementService
{
    public async Task<bool> DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await dbContext.Documents.SingleOrDefaultAsync(document => document.Id == documentId, cancellationToken);
        if (document is null)
        {
            return false;
        }

        await vectorStore.DeleteDocumentAsync(document.Id, cancellationToken);
        await storage.DeleteAsync(document.ObjectKey, cancellationToken);
        dbContext.Documents.Remove(document);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Deleted document {DocumentId} ({FileName}), original object, and vector points.",
            document.Id,
            document.FileName);

        return true;
    }

    public async Task<DocumentStatusResponse?> QueueReindexAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await dbContext.Documents.SingleOrDefaultAsync(document => document.Id == documentId, cancellationToken);
        if (document is null)
        {
            return null;
        }

        document.Status = DocumentStatus.Pending;
        document.ErrorMessage = null;
        document.ChunkCount = 0;
        document.ProgressStage = "Queued";
        document.ProgressPercent = 0;
        document.ProcessedChunks = 0;
        document.TotalChunks = 0;
        document.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Queued document {DocumentId} ({FileName}) for reindex.",
            document.Id,
            document.FileName);

        return ToStatusResponse(document);
    }

    private static DocumentStatusResponse ToStatusResponse(DocumentRecord document)
    {
        return new DocumentStatusResponse(
            document.Id,
            document.FileName,
            document.ContentType,
            document.Status.ToString(),
            document.ChunkCount,
            document.ProgressStage,
            document.ProgressPercent,
            document.ProcessedChunks,
            document.TotalChunks,
            document.ErrorMessage,
            document.CreatedAtUtc,
            document.UpdatedAtUtc);
    }
}
