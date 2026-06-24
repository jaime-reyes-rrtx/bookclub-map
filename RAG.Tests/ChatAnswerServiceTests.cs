using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
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
    public async Task AskAsync_BroadQuestionExpandsRetrievalAndReturnsContextCitations()
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
        Assert.Equal(chunks.Count, response.Citations.Count);
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

    [Fact]
    public async Task AskAsync_ComparisonQuestionRetrievesEvidenceForEachNamedSubject()
    {
        var calpurniaDocumentId = Guid.NewGuid();
        var harryPotterDocumentId = Guid.NewGuid();
        var calpurniaProfile = new RetrievedChunk(
            calpurniaDocumentId,
            "A Squirrelly Situation - Calpurnia Tate.pdf",
            -2,
            null,
            "objects/calpurnia.pdf",
            "Literary artifact: book-club profile\nLikely protagonists: Calpurnia Tate\nMajor characters: Calpurnia Tate, Travis.\nThemes: curiosity, observation, learning.",
            .68,
            "literary_book_club_profile",
            "Book club literary profile");
        var eisenhornProfile = new RetrievedChunk(
            Guid.NewGuid(),
            "Eisenhorn.pdf",
            -2,
            null,
            "objects/eisenhorn.pdf",
            "Literary artifact: book-club profile\nLikely protagonists: Gregor Eisenhorn.",
            .66,
            "literary_book_club_profile",
            "Book club literary profile");
        var hermioneProfile = new RetrievedChunk(
            harryPotterDocumentId,
            "Harry Potter and the Philosopher's Stone.pdf",
            -2,
            null,
            "objects/harry-potter.pdf",
            "Literary artifact: book-club profile\nMajor characters: Harry Potter, Ron Weasley, Hermione Granger.\nThemes: friendship, courage, learning.",
            .61,
            "literary_book_club_profile",
            "Book club literary profile");
        var hermioneSource = new RetrievedChunk(
            harryPotterDocumentId,
            "Harry Potter and the Philosopher's Stone.pdf",
            8,
            72,
            "objects/harry-potter.pdf",
            "Hermione Granger uses study, logic, and courage to help solve problems with Harry and Ron.",
            .42);
        var chat = new RecordingChatProvider("Calpurnia and Hermione are both curious, capable young learners [1][2].");
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new SequencedVectorStore([
                [calpurniaProfile, eisenhornProfile],
                [],
                [],
                [],
                [],
                [calpurniaProfile],
                [calpurniaProfile],
                [hermioneProfile],
                [hermioneProfile, hermioneSource]
            ]),
            chat);

        var response = await service.AskAsync(
            new AskRequest("Can you find any similarities between Calpurnia and Hermione?", null),
            CancellationToken.None);

        Assert.Contains(chat.LastChunks, chunk => chunk.DocumentId == calpurniaDocumentId);
        Assert.Contains(chat.LastChunks, chunk => chunk.DocumentId == harryPotterDocumentId);
        Assert.Contains(chat.LastChunks, chunk => chunk.Text.Contains("Hermione Granger uses study"));
        Assert.DoesNotContain(chat.LastChunks, chunk => chunk.FileName == "Eisenhorn.pdf");
        Assert.Contains(response.Citations, citation => citation.FileName.Contains("Calpurnia"));
        Assert.Contains(response.Citations, citation => citation.FileName.Contains("Harry Potter"));
        Assert.Contains("Calpurnia and Hermione", response.Answer);
    }

    [Fact]
    public async Task AskAsync_ReturnsCitationProvenanceForGeneratedArtifacts()
    {
        var chunk = new RetrievedChunk(
            Guid.NewGuid(),
            "novel.txt",
            -2,
            null,
            "objects/novel.txt",
            "Likely protagonists: Alice Morgan.",
            .97,
            "literary_book_club_profile",
            "Book club literary profile",
            new ChunkProvenance(IsGenerated: true, ArtifactKind: "book-club-profile"));
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore([chunk]),
            new StubChatProvider("Alice Morgan is central [1]."));

        var response = await service.AskAsync(new AskRequest("Who is Alice Morgan?", null), CancellationToken.None);

        var citation = Assert.Single(response.Citations);
        Assert.True(citation.IsGeneratedArtifact);
        Assert.Equal("literary_book_club_profile", citation.ChunkType);
        Assert.Equal("book-club-profile", citation.ArtifactKind);
        Assert.Equal("Book club literary profile", citation.Title);
    }

    [Fact]
    public async Task AskAsync_IncludesDiagnosticsWhenRequested()
    {
        var chunk = new RetrievedChunk(
            Guid.NewGuid(),
            "guide.txt",
            0,
            1,
            "objects/guide.txt",
            "Qdrant stores the searchable vectors.",
            .88);
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore([chunk]),
            new StubChatProvider("Qdrant stores vectors [1]."));

        var response = await service.AskAsync(
            new AskRequest("Where are vectors stored?", null, IncludeDiagnostics: true),
            CancellationToken.None);

        Assert.NotNull(response.Diagnostics);
        Assert.Contains(response.Diagnostics.Queries, query => query.Text == "Where are vectors stored?");
        Assert.Contains(response.Diagnostics.Candidates, candidate => candidate.FileName == "guide.txt" && candidate.Selected);
        Assert.Contains(response.Diagnostics.SelectedContext, selected => selected.Snippet.Contains("Qdrant"));
    }

    [Fact]
    public async Task AskAsync_RejectsQuestionsOverConfiguredLimit()
    {
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore([]),
            new StubChatProvider("unused"),
            Options.Create(new RagOptions
            {
                Request = new RequestOptions { MaxQuestionCharacters = 8 }
            }));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AskAsync(new AskRequest("This question is too long.", null), CancellationToken.None));

        Assert.Contains("8 characters", exception.Message);
    }

    [Fact]
    public async Task AskAsync_RejectsTooManySelectedDocuments()
    {
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore([]),
            new StubChatProvider("unused"),
            Options.Create(new RagOptions
            {
                Request = new RequestOptions { MaxSelectedDocuments = 1 }
            }));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AskAsync(new AskRequest("What happened?", [Guid.NewGuid(), Guid.NewGuid()]), CancellationToken.None));

        Assert.Contains("1 documents", exception.Message);
    }

    [Fact]
    public async Task AskAsync_CapsExpandedRetrievalQueries()
    {
        var vectorStore = new StubVectorStore([]);
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            vectorStore,
            new StubChatProvider("unused"),
            Options.Create(new RagOptions
            {
                Request = new RequestOptions { MaxRetrievalQueries = 2 }
            }));

        await service.AskAsync(
            new AskRequest("Compare Harry Potter and Hermione Granger as protagonists.", null, IncludeDiagnostics: true),
            CancellationToken.None);

        Assert.Equal(2, vectorStore.SearchCount);
    }

    [Fact]
    public async Task AskAsync_DiagnosticsExplainComparisonThresholdFiltering()
    {
        var lowRankChunk = new RetrievedChunk(
            Guid.NewGuid(),
            "unrelated.txt",
            0,
            null,
            "objects/unrelated.txt",
            "A generic passage without either named subject.",
            .2);
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore([lowRankChunk]),
            new StubChatProvider("unused"));

        var response = await service.AskAsync(
            new AskRequest("Compare Alice Morgan and Bruno Stone.", null, IncludeDiagnostics: true),
            CancellationToken.None);

        var candidate = Assert.Single(response.Diagnostics!.Candidates);
        Assert.False(candidate.Selected);
        Assert.Contains("filtered below comparison rank threshold", candidate.RankReasons);
    }

    [Fact]
    public async Task AskAsync_ProviderTimeoutCancelsSlowProvider()
    {
        var chunk = new RetrievedChunk(
            Guid.NewGuid(),
            "guide.txt",
            0,
            null,
            "objects/guide.txt",
            "Qdrant stores vectors.",
            .9);
        var service = new ChatAnswerService(
            new StubEmbeddingProvider(),
            new StubVectorStore([chunk]),
            new SlowChatProvider(),
            Options.Create(new RagOptions
            {
                Request = new RequestOptions { ProviderTimeoutSeconds = 1 }
            }));

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            service.AskAsync(new AskRequest("Where are vectors stored?", null), CancellationToken.None));
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

    private sealed class SlowChatProvider : IChatCompletionProvider
    {
        public async Task<string> GenerateAnswerAsync(string question, IReadOnlyList<RetrievedChunk> chunks, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return "too slow";
        }
    }

    private sealed class RecordingChatProvider(string answer) : IChatCompletionProvider
    {
        public IReadOnlyList<RetrievedChunk> LastChunks { get; private set; } = [];

        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<RetrievedChunk> chunks, CancellationToken cancellationToken)
        {
            LastChunks = chunks;
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

        public Task<IReadOnlyList<RetrievedChunk>> GetDocumentProfileChunksAsync(
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)chunks
                .Where(chunk => chunk.ChunkIndex < 0 || chunk.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetChunksContainingTextAsync(
            IReadOnlyCollection<string> terms,
            IReadOnlyCollection<Guid>? documentIds,
            int limitPerTerm,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)chunks
                .Where(chunk => terms.Any(term => chunk.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(terms.Count * limitPerTerm)
                .ToList());
        }
    }

    private sealed class SequencedVectorStore(IReadOnlyList<IReadOnlyList<RetrievedChunk>> searchResults) : IVectorStore
    {
        private int _searchCount;

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
            var index = Math.Min(_searchCount, searchResults.Count - 1);
            _searchCount++;
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)searchResults[index].Take(limit).ToList());
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetDocumentProfileChunksAsync(
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)searchResults
                .SelectMany(chunks => chunks)
                .Where(chunk => chunk.ChunkIndex < 0 || chunk.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(chunk => $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}")
                .ToList());
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetChunksContainingTextAsync(
            IReadOnlyCollection<string> terms,
            IReadOnlyCollection<Guid>? documentIds,
            int limitPerTerm,
            CancellationToken cancellationToken)
        {
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)searchResults
                .SelectMany(chunks => chunks)
                .Where(chunk => terms.Any(term => chunk.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(chunk => $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}")
                .Take(terms.Count * limitPerTerm)
                .ToList());
        }
    }
}
