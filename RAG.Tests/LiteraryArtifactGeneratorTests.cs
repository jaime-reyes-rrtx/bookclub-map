using Microsoft.Extensions.Logging.Abstractions;
using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class LiteraryArtifactGeneratorTests
{
    [Fact]
    public async Task GenerateArtifactsAsync_CreatesNameProfileAndBookClubProfile()
    {
        var documentId = Guid.NewGuid();
        var provider = new RecordingLiteraryAnalysisProvider("Likely protagonists: Alice Morgan\nThemes: curiosity.");
        var generator = new LiteraryArtifactGenerator(provider, NullLogger<LiteraryArtifactGenerator>.Instance);
        var sourceChunks = new[]
        {
            new TextChunk(Guid.NewGuid(), documentId, "novel.txt", "objects/novel.txt", 0, 1, "Alice Morgan met Bruno Stone in the library."),
            new TextChunk(Guid.NewGuid(), documentId, "novel.txt", "objects/novel.txt", 1, 2, "Alice Morgan wondered why Bruno Stone returned.")
        };
        var extracted = new ExtractedDocument([
            new ExtractedPage(1, "Alice Morgan met Bruno Stone."),
            new ExtractedPage(2, "Alice Morgan asked Clara Vale for help.")
        ]);

        var artifacts = await generator.GenerateArtifactsAsync(
            documentId,
            "novel.txt",
            "objects/novel.txt",
            extracted,
            sourceChunks,
            CancellationToken.None);

        Assert.Equal(2, artifacts.Count);
        Assert.Contains(artifacts, chunk => chunk.ChunkType == "literary_name_profile" && chunk.Text.Contains("Alice Morgan"));
        Assert.Contains(artifacts, chunk => chunk.ChunkType == "literary_book_club_profile" && chunk.Text.Contains("Themes: curiosity."));
        Assert.Contains("Alice Morgan", provider.CandidateNames);
        Assert.Equal(2, provider.Excerpts.Count);
    }

    [Fact]
    public async Task GenerateArtifactsAsync_ContinuesWithNameProfile_WhenBookClubProfileFails()
    {
        var documentId = Guid.NewGuid();
        var generator = new LiteraryArtifactGenerator(
            new ThrowingLiteraryAnalysisProvider(),
            NullLogger<LiteraryArtifactGenerator>.Instance);
        var extracted = new ExtractedDocument([new ExtractedPage(1, "Alice Morgan begins the journey.")]);

        var artifacts = await generator.GenerateArtifactsAsync(
            documentId,
            "novel.txt",
            "objects/novel.txt",
            extracted,
            [],
            CancellationToken.None);

        var artifact = Assert.Single(artifacts);
        Assert.Equal("literary_name_profile", artifact.ChunkType);
        Assert.Contains("Alice Morgan", artifact.Text);
    }

    private sealed class RecordingLiteraryAnalysisProvider(string profile) : ILiteraryAnalysisProvider
    {
        public IReadOnlyList<string> CandidateNames { get; private set; } = [];
        public IReadOnlyList<string> Excerpts { get; private set; } = [];

        public Task<string> GenerateBookClubProfileAsync(
            string fileName,
            IReadOnlyList<string> candidateNames,
            IReadOnlyList<string> excerpts,
            CancellationToken cancellationToken)
        {
            CandidateNames = candidateNames;
            Excerpts = excerpts;
            return Task.FromResult(profile);
        }
    }

    private sealed class ThrowingLiteraryAnalysisProvider : ILiteraryAnalysisProvider
    {
        public Task<string> GenerateBookClubProfileAsync(
            string fileName,
            IReadOnlyList<string> candidateNames,
            IReadOnlyList<string> excerpts,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("profile failed");
        }
    }
}
