using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class ChatAnswerServiceTests
{
    [Fact]
    public async Task AskAsync_ReturnsCitationsFromRetrievedChunks()
    {
        var chunks = new[]
        {
            new RetrievedChunk(
                Guid.NewGuid(),
                "guide.txt",
                2,
                null,
                "objects/guide.txt",
                "The pipeline stores vectors in Qdrant and original files in object storage.",
                .91)
        };
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore(chunks),
            new StubChatProvider("Use Qdrant for vectors."));

        var response = await service.AskAsync(new AskRequest("Where are vectors stored?", null), CancellationToken.None);

        Assert.Equal("Use Qdrant for vectors.", response.Answer);
        var citation = Assert.Single(response.Citations);
        Assert.Equal("guide.txt", citation.FileName);
        Assert.Equal(2, citation.ChunkIndex);
        Assert.Contains("Qdrant", citation.Snippet);
    }

    [Fact]
    public async Task AskAsync_BroadQuestionExpandsRetrievalAndCapsCitations()
    {
        var chunks = Enumerable.Range(0, 7)
            .Select(index => new RetrievedChunk(
                Guid.NewGuid(),
                "novel.txt",
                index,
                null,
                "objects/novel.txt",
                $"Character evidence chunk {index}",
                .9 - (index * .01)))
            .ToList();
        var vectorStore = new StubVectorStore(chunks);
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            vectorStore,
            new StubChatProvider("Harry Potter, Ron Weasley, and Hermione Granger are the central protagonists [1]."));

        var response = await service.AskAsync(
            new AskRequest("Name the protagonists of the novel", null),
            CancellationToken.None);

        Assert.True(vectorStore.SearchCount > 1);
        Assert.Equal(5, response.Citations.Count);
        Assert.Contains("Harry Potter", response.Answer);
    }

    [Fact]
    public async Task AskAsync_UsesDocumentProfileForProtagonistQuestion()
    {
        var chunks = new[]
        {
            new RetrievedChunk(
                Guid.NewGuid(),
                "Novel.pdf",
                -2,
                null,
                "objects/novel.pdf",
                "Literary artifact: book-club profile\nLikely protagonists: Harry Potter, Ron Weasley, Hermione Granger\nMajor characters: Harry Potter, Ron Weasley, Hermione Granger, Hagrid.",
                .98,
                "literary_book_club_profile",
                "Book club literary profile")
        };
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore(chunks),
            new StubChatProvider("This should not be used."));

        var response = await service.AskAsync(
            new AskRequest("Name the protagonists of the first Harry Potter Novel", null),
            CancellationToken.None);

        Assert.Contains("Harry Potter", response.Answer);
        Assert.Contains("Ron Weasley", response.Answer);
        Assert.Contains("Hermione Granger", response.Answer);
        Assert.DoesNotContain("This should not be used", response.Answer);
    }

    [Fact]
    public async Task AskAsync_UsesBulletedDocumentProfileForProtagonistQuestion()
    {
        var chunks = new[]
        {
            new RetrievedChunk(
                Guid.NewGuid(),
                "Novel.pdf",
                -2,
                null,
                "objects/novel.pdf",
                """
                Literary artifact: book-club profile
                Likely protagonists:
                * Harry (main character)
                * Ron Weasley
                * Hermione Granger

                Major characters:
                * Hagrid
                * Snape
                """,
                .98,
                "literary_book_club_profile",
                "Book club literary profile")
        };
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore(chunks),
            new StubChatProvider("This should not be used."));

        var response = await service.AskAsync(
            new AskRequest("Name the protagonists of the first Harry Potter Novel", null),
            CancellationToken.None);

        Assert.Contains("Harry", response.Answer);
        Assert.Contains("Ron Weasley", response.Answer);
        Assert.Contains("Hermione Granger", response.Answer);
        Assert.DoesNotContain("Hagrid", response.Answer);
        Assert.DoesNotContain("This should not be used", response.Answer);
    }


    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken)
        {
            return Task.FromResult(new[] { 0.1f, 0.2f, 0.3f });
        }
    }

    private sealed class StubChatProvider(string answer) : IChatCompletionProvider
    {
        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<RetrievedChunk> chunks, CancellationToken cancellationToken)
        {
            return Task.FromResult(answer);
        }
    }

    private sealed class StubVectorStore(IReadOnlyList<RetrievedChunk> chunks) : IVectorStore
    {
        public int SearchCount { get; private set; }

        public Task EnsureCollectionAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UpsertChunksAsync(IReadOnlyList<EmbeddedChunk> chunksToUpsert, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
            float[] embedding,
            int limit,
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            SearchCount++;
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)chunks.Take(limit).ToList());
        }
    }
}
