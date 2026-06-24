using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Data;
using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class DocumentIngestionServiceTests
{
    [Fact]
    public async Task IngestDocumentAsync_IndexesPendingDocumentAndUpdatesProgress()
    {
        await using var dbContext = CreateDbContext();
        var document = AddDocument(dbContext, DocumentStatus.Pending);
        var storage = new InMemoryObjectStorage("Alice Morgan meets Bruno Stone in the library. Alice learns the truth.");
        var vectorStore = new RecordingVectorStore();
        var service = new DocumentIngestionService(
            dbContext,
            storage,
            new TextExtractor(),
            new TokenTextChunker(Options.Create(new RagOptions
            {
                Ingestion = new IngestionOptions
                {
                    ChunkTokenCount = 50,
                    ChunkOverlapTokens = 10
                }
            })),
            new StaticArtifactGenerator(),
            new CountingEmbeddingProvider(),
            vectorStore,
            NullLogger<DocumentIngestionService>.Instance);

        await service.IngestDocumentAsync(document.Id, CancellationToken.None);

        Assert.Equal(DocumentStatus.Indexed, document.Status);
        Assert.Equal("Ready", document.ProgressStage);
        Assert.Equal(100, document.ProgressPercent);
        Assert.Equal(document.ChunkCount, document.ProcessedChunks);
        Assert.Equal(document.ChunkCount, document.TotalChunks);
        Assert.True(document.ChunkCount >= 2);
        Assert.True(storage.EnsureReadyCalled);
        Assert.True(vectorStore.EnsureCollectionCalled);
        Assert.Equal(document.Id, vectorStore.DeletedDocumentId);
        Assert.Equal(document.ChunkCount, vectorStore.UpsertedChunks.Count);
        Assert.Contains(vectorStore.UpsertedChunks, chunk => chunk.Chunk.ChunkType == "literary_book_club_profile");
    }

    [Fact]
    public async Task IngestDocumentAsync_MarksDocumentFailed_WhenDependencyFails()
    {
        await using var dbContext = CreateDbContext();
        var document = AddDocument(dbContext, DocumentStatus.Pending);
        var service = new DocumentIngestionService(
            dbContext,
            new FailingObjectStorage(),
            new TextExtractor(),
            new TokenTextChunker(Options.Create(new RagOptions())),
            new StaticArtifactGenerator(),
            new CountingEmbeddingProvider(),
            new RecordingVectorStore(),
            NullLogger<DocumentIngestionService>.Instance);

        await service.IngestDocumentAsync(document.Id, CancellationToken.None);

        Assert.Equal(DocumentStatus.Failed, document.Status);
        Assert.Equal("Failed", document.ProgressStage);
        Assert.Contains("storage unavailable", document.ErrorMessage);
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

    private sealed class InMemoryObjectStorage(string content) : IObjectStorage
    {
        public bool EnsureReadyCalled { get; private set; }

        public Task EnsureReadyAsync(CancellationToken cancellationToken)
        {
            EnsureReadyCalled = true;
            return Task.CompletedTask;
        }

        public Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        }
    }

    private sealed class FailingObjectStorage : IObjectStorage
    {
        public Task EnsureReadyAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("storage unavailable");
        }

        public Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(Stream.Null);
        }
    }

    private sealed class StaticArtifactGenerator : ILiteraryArtifactGenerator
    {
        public Task<IReadOnlyList<TextChunk>> GenerateArtifactsAsync(
            Guid documentId,
            string fileName,
            string objectKey,
            ExtractedDocument document,
            IReadOnlyList<TextChunk> sourceChunks,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<TextChunk> artifacts =
            [
                new TextChunk(
                    Guid.NewGuid(),
                    documentId,
                    fileName,
                    objectKey,
                    -2,
                    null,
                    "Likely protagonists: Alice Morgan",
                    "literary_book_club_profile",
                    "Book club literary profile")
            ];

            return Task.FromResult(artifacts);
        }
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken)
        {
            return Task.FromResult(new[] { 0.1f, 0.2f, 0.3f });
        }
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public bool EnsureCollectionCalled { get; private set; }
        public Guid? DeletedDocumentId { get; private set; }
        public IReadOnlyList<EmbeddedChunk> UpsertedChunks { get; private set; } = [];

        public Task EnsureCollectionAsync(CancellationToken cancellationToken)
        {
            EnsureCollectionCalled = true;
            return Task.CompletedTask;
        }

        public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
        {
            DeletedDocumentId = documentId;
            return Task.CompletedTask;
        }

        public Task UpsertChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken)
        {
            UpsertedChunks = chunks;
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
