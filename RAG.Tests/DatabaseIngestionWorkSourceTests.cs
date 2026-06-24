using Microsoft.EntityFrameworkCore;
using RAG.Core.Data;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class DatabaseIngestionWorkSourceTests
{
    [Fact]
    public async Task GetNextDocumentIdsAsync_ReturnsPendingAndStaleProcessingDocuments()
    {
        await using var dbContext = CreateDbContext();
        var pending = AddDocument(dbContext, DocumentStatus.Pending, DateTimeOffset.UtcNow.AddMinutes(-10));
        var staleProcessing = AddDocument(dbContext, DocumentStatus.Processing, DateTimeOffset.UtcNow.AddMinutes(-3));
        var freshProcessing = AddDocument(dbContext, DocumentStatus.Processing, DateTimeOffset.UtcNow);
        var indexed = AddDocument(dbContext, DocumentStatus.Indexed, DateTimeOffset.UtcNow.AddMinutes(-20));
        var source = new DatabaseIngestionWorkSource(dbContext);

        var documentIds = await source.GetNextDocumentIdsAsync(CancellationToken.None);

        Assert.Contains(pending.Id, documentIds);
        Assert.Contains(staleProcessing.Id, documentIds);
        Assert.DoesNotContain(freshProcessing.Id, documentIds);
        Assert.DoesNotContain(indexed.Id, documentIds);
    }

    private static RagDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<RagDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var dbContext = new RagDbContext(options);
        dbContext.Database.OpenConnection();
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static DocumentRecord AddDocument(RagDbContext dbContext, DocumentStatus status, DateTimeOffset updatedAtUtc)
    {
        var document = new DocumentRecord
        {
            Id = Guid.NewGuid(),
            FileName = $"{status}-{Guid.NewGuid():N}.txt",
            ContentType = "text/plain",
            ObjectKey = $"objects/{Guid.NewGuid():N}.txt",
            Status = status,
            CreatedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc
        };

        dbContext.Documents.Add(document);
        dbContext.SaveChanges();
        return document;
    }
}
