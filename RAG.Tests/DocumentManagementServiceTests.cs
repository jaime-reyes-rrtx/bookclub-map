using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RAG.Core.Data;
using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class DocumentManagementServiceTests
{
    [Fact]
    public async Task DeleteDocumentAsync_RemovesMetadataObjectAndVectors()
    {
        await using var dbContext = CreateDbContext();
        var document = AddDocument(dbContext, DocumentStatus.Indexed);
        var storage = new RecordingObjectStorage();
        var vectorStore = new RecordingVectorStore();
        var service = new DocumentManagementService(
            dbContext,
            storage,
            vectorStore,
            NullLogger<DocumentManagementService>.Instance);

        var deleted = await service.DeleteDocumentAsync(document.Id, CancellationToken.None);

        Assert.True(deleted);
        Assert.Null(await dbContext.Documents.SingleOrDefaultAsync(item => item.Id == document.Id));
        Assert.Equal(document.ObjectKey, storage.DeletedObjectKey);
        Assert.Equal(document.Id, vectorStore.DeletedDocumentId);
    }

    [Fact]
    public async Task QueueReindexAsync_ResetsProgressAndMarksPending()
    {
        await using var dbContext = CreateDbContext();
        var document = AddDocument(dbContext, DocumentStatus.Failed);
        document.ErrorMessage = "failed";
        document.ChunkCount = 12;
        document.ProgressStage = "Failed";
        document.ProgressPercent = 50;
        document.ProcessedChunks = 3;
        document.TotalChunks = 12;
        await dbContext.SaveChangesAsync();
        var service = new DocumentManagementService(
            dbContext,
            new RecordingObjectStorage(),
            new RecordingVectorStore(),
            NullLogger<DocumentManagementService>.Instance);

        var response = await service.QueueReindexAsync(document.Id, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal("Pending", response.Status);
        Assert.Equal(0, response.ChunkCount);
        Assert.Equal("Queued", response.ProgressStage);
        Assert.Equal(0, response.ProgressPercent);
        Assert.Null(response.ErrorMessage);
        Assert.Equal(DocumentStatus.Pending, document.Status);
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

    private static DocumentRecord AddDocument(RagDbContext dbContext, DocumentStatus status)
    {
        var document = new DocumentRecord
        {
            Id = Guid.NewGuid(),
            FileName = "novel.txt",
            ContentType = "text/plain",
            ObjectKey = "objects/novel.txt",
            Status = status,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        dbContext.Documents.Add(document);
        dbContext.SaveChanges();
        return document;
    }

    private sealed class RecordingObjectStorage : IObjectStorage
    {
        public string? DeletedObjectKey { get; private set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("content")));
        }

        public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            DeletedObjectKey = objectKey;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public Guid? DeletedDocumentId { get; private set; }

        public Task EnsureCollectionAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
        {
            DeletedDocumentId = documentId;
            return Task.CompletedTask;
        }

        public Task UpsertChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
            float[] embedding,
            int limit,
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)[]);
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetDocumentProfileChunksAsync(
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)[]);
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetChunksContainingTextAsync(
            IReadOnlyCollection<string> terms,
            IReadOnlyCollection<Guid>? documentIds,
            int limitPerTerm,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)[]);
        }
    }
}
