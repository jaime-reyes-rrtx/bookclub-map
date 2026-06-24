using Microsoft.EntityFrameworkCore;
using RAG.Core.Data;

namespace RAG.Core.Services;

public sealed class DatabaseIngestionWorkSource(RagDbContext dbContext) : IIngestionWorkSource
{
    public async Task<IReadOnlyList<Guid>> GetNextDocumentIdsAsync(CancellationToken cancellationToken)
    {
        var staleProcessingCutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        var pendingDocuments = await dbContext.Documents
            .Where(document => document.Status == DocumentStatus.Pending)
            .ToListAsync(cancellationToken);
        var staleProcessingDocuments = (await dbContext.Documents
                .Where(document => document.Status == DocumentStatus.Processing)
                .ToListAsync(cancellationToken))
            .Where(document => document.UpdatedAtUtc < staleProcessingCutoff);

        return pendingDocuments
            .Concat(staleProcessingDocuments)
            .OrderBy(document => document.CreatedAtUtc)
            .Take(5)
            .Select(document => document.Id)
            .ToList();
    }
}
