using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests.Evaluation;

public sealed class RagEvaluationTests
{
    private static readonly Guid GardenDocumentId = Guid.NewGuid();
    private static readonly Guid SpaceDocumentId = Guid.NewGuid();
    private static readonly Guid MysteryDocumentId = Guid.NewGuid();

    public static TheoryData<RagEvaluationCase> GoldenCases()
    {
        return new TheoryData<RagEvaluationCase>
        {
            new(
                "direct factual question",
                "Where are tomato seeds stored?",
                null,
                ["garden-notes.txt"],
                ["tomato seeds", "cool pantry"],
                RequiresSourceCitation: true,
                AllowsGeneratedArtifactCitation: false),
            new(
                "broad literary question",
                "Name the protagonists of the mystery novel",
                [MysteryDocumentId],
                ["mystery-novel.txt"],
                ["Alice Morgan", "Bruno Stone"],
                RequiresSourceCitation: false,
                AllowsGeneratedArtifactCitation: true),
            new(
                "cross-document comparison",
                "Compare tomato gardens and moon gardens",
                null,
                ["garden-notes.txt", "space-gardens.txt"],
                ["tomato gardens", "moon gardens"],
                RequiresSourceCitation: true,
                AllowsGeneratedArtifactCitation: false),
            new(
                "selected document constraint",
                "What do moon gardens use?",
                [SpaceDocumentId],
                ["space-gardens.txt"],
                ["moon gardens"],
                RequiresSourceCitation: true,
                AllowsGeneratedArtifactCitation: false),
            new(
                "generated artifact helps",
                "Who are the main characters in the mystery novel?",
                [MysteryDocumentId],
                ["mystery-novel.txt"],
                ["Likely protagonists", "Alice Morgan"],
                RequiresSourceCitation: true,
                AllowsGeneratedArtifactCitation: true)
        };
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task GoldenCase_SelectsExpectedContextAndCitationTypes(RagEvaluationCase evaluationCase)
    {
        var service = new ChatAnswerService(
            new DeterministicEmbeddingProvider(),
            new GoldenVectorStore(CreateGoldenChunks()),
            new EchoContextChatProvider());

        var response = await service.AskAsync(
            new AskRequest(evaluationCase.Question, evaluationCase.DocumentIds?.ToArray(), IncludeDiagnostics: true),
            CancellationToken.None);

        Assert.NotNull(response.Diagnostics);
        foreach (var expectedFileName in evaluationCase.ExpectedFileNames)
        {
            Assert.Contains(response.Diagnostics.SelectedContext, context => context.FileName == expectedFileName);
        }

        var selectedText = string.Join('\n', response.Diagnostics.SelectedContext.Select(context => context.Snippet));
        foreach (var expectedTerm in evaluationCase.ExpectedTermsInSelectedContext)
        {
            Assert.Contains(expectedTerm, selectedText, StringComparison.OrdinalIgnoreCase);
        }

        if (evaluationCase.RequiresSourceCitation)
        {
            Assert.Contains(response.Citations, citation => !citation.IsGeneratedArtifact);
        }

        if (!evaluationCase.AllowsGeneratedArtifactCitation)
        {
            Assert.DoesNotContain(response.Citations, citation => citation.IsGeneratedArtifact);
        }
    }

    [Fact]
    public async Task GoldenCase_NoMatchingEvidenceReturnsGracefulNoContextAnswer()
    {
        var service = new ChatAnswerService(
            new DeterministicEmbeddingProvider(),
            new GoldenVectorStore(CreateGoldenChunks()),
            new EchoContextChatProvider());

        var response = await service.AskAsync(
            new AskRequest("What does the archive say about volcano engines?", null, IncludeDiagnostics: true),
            CancellationToken.None);

        Assert.Equal("No indexed document chunks matched the question.", response.Answer);
        Assert.Empty(response.Citations);
        Assert.Empty(response.Diagnostics!.SelectedContext);
    }

    private static IReadOnlyList<RetrievedChunk> CreateGoldenChunks()
    {
        return
        [
            new RetrievedChunk(
                GardenDocumentId,
                "garden-notes.txt",
                0,
                1,
                "objects/garden.txt",
                "Tomato seeds are stored in a labeled jar in the cool pantry. Tomato gardens need steady water.",
                .92),
            new RetrievedChunk(
                SpaceDocumentId,
                "space-gardens.txt",
                0,
                1,
                "objects/space.txt",
                "Moon gardens use sealed trays, careful light, and recycled water for seedlings.",
                .9),
            new RetrievedChunk(
                MysteryDocumentId,
                "mystery-novel.txt",
                -2,
                null,
                "objects/mystery.txt",
                "Literary artifact: book-club profile\nLikely protagonists: Alice Morgan, Bruno Stone\nThemes: curiosity and trust.",
                .94,
                "literary_book_club_profile",
                "Book club literary profile",
                new ChunkProvenance(IsGenerated: true, ArtifactKind: "book-club-profile")),
            new RetrievedChunk(
                MysteryDocumentId,
                "mystery-novel.txt",
                4,
                12,
                "objects/mystery.txt",
                "Alice Morgan follows Bruno Stone through the archive and finds the missing map.",
                .84)
        ];
    }

    public sealed record RagEvaluationCase(
        string Name,
        string Question,
        IReadOnlyList<Guid>? DocumentIds,
        IReadOnlyList<string> ExpectedFileNames,
        IReadOnlyList<string> ExpectedTermsInSelectedContext,
        bool RequiresSourceCitation,
        bool AllowsGeneratedArtifactCitation);

    private sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
    {
        public Task<float[]> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken)
        {
            var lower = input.ToLowerInvariant();
            if (lower.Contains("volcano"))
            {
                return Task.FromResult(new[] { 0f });
            }

            if (lower.Contains("mystery") || lower.Contains("alice") || lower.Contains("bruno"))
            {
                return Task.FromResult(new[] { 3f });
            }

            if (lower.Contains("moon") || lower.Contains("space"))
            {
                return Task.FromResult(new[] { 2f });
            }

            return Task.FromResult(new[] { 1f });
        }
    }

    private sealed class GoldenVectorStore(IReadOnlyList<RetrievedChunk> chunks) : IVectorStore
    {
        public Task EnsureCollectionAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task UpsertChunksAsync(IReadOnlyList<EmbeddedChunk> chunksToUpsert, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
            float[] embedding,
            int limit,
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            var matches = embedding[0] switch
            {
                0f => [],
                1f => chunks.Where(chunk => chunk.DocumentId == GardenDocumentId),
                2f => chunks.Where(chunk => chunk.DocumentId == GardenDocumentId || chunk.DocumentId == SpaceDocumentId),
                3f => chunks.Where(chunk => chunk.DocumentId == MysteryDocumentId),
                _ => chunks
            };

            return Task.FromResult((IReadOnlyList<RetrievedChunk>)ApplyDocumentFilter(matches, documentIds).Take(limit).ToList());
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetDocumentProfileChunksAsync(
            IReadOnlyCollection<Guid>? documentIds,
            CancellationToken cancellationToken)
        {
            var matches = chunks.Where(chunk => chunk.Provenance?.IsGenerated == true);
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)ApplyDocumentFilter(matches, documentIds).ToList());
        }

        public Task<IReadOnlyList<RetrievedChunk>> GetChunksContainingTextAsync(
            IReadOnlyCollection<string> terms,
            IReadOnlyCollection<Guid>? documentIds,
            int limitPerTerm,
            CancellationToken cancellationToken)
        {
            var matches = chunks.Where(chunk => terms.Any(term => chunk.Text.Contains(term, StringComparison.OrdinalIgnoreCase)));
            return Task.FromResult((IReadOnlyList<RetrievedChunk>)ApplyDocumentFilter(matches, documentIds).Take(terms.Count * limitPerTerm).ToList());
        }

        private static IEnumerable<RetrievedChunk> ApplyDocumentFilter(
            IEnumerable<RetrievedChunk> matches,
            IReadOnlyCollection<Guid>? documentIds)
        {
            return documentIds is { Count: > 0 }
                ? matches.Where(chunk => documentIds.Contains(chunk.DocumentId))
                : matches;
        }
    }

    private sealed class EchoContextChatProvider : IChatCompletionProvider
    {
        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<RetrievedChunk> chunks, CancellationToken cancellationToken)
        {
            return Task.FromResult($"Selected {chunks.Count} context chunks.");
        }
    }
}
