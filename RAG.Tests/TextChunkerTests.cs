using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class TextChunkerTests
{
    [Fact]
    public void Chunk_CreatesOverlappingChunksWithStableMetadata()
    {
        var options = Options.Create(new RagOptions
        {
            Ingestion = new IngestionOptions
            {
                ChunkTokenCount = 50,
                ChunkOverlapTokens = 10
            }
        });
        var chunker = new TokenTextChunker(options);
        var documentId = Guid.NewGuid();
        var text = string.Join(' ', Enumerable.Range(1, 120).Select(i => $"token{i}"));
        var extracted = new ExtractedDocument([
            new ExtractedPage(7, text)
        ]);

        var chunks = chunker.Chunk(documentId, "sample.txt", "objects/sample.txt", extracted);

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("token1 token2", chunks[0].Text);
        Assert.StartsWith("token41 token42", chunks[1].Text);
        Assert.Equal(documentId, chunks[0].DocumentId);
        Assert.Equal("sample.txt", chunks[0].FileName);
        Assert.Equal(7, chunks[0].PageNumber);
    }

    [Fact]
    public void Chunk_ReturnsEmptyListForEmptyDocument()
    {
        var chunker = new TokenTextChunker(Options.Create(new RagOptions()));
        var chunks = chunker.Chunk(Guid.NewGuid(), "empty.txt", "objects/empty.txt", new ExtractedDocument([]));

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_ClampsSmallChunkSizeAndLargeOverlap()
    {
        var options = Options.Create(new RagOptions
        {
            Ingestion = new IngestionOptions
            {
                ChunkTokenCount = 10,
                ChunkOverlapTokens = 100
            }
        });
        var chunker = new TokenTextChunker(options);
        var text = string.Join(' ', Enumerable.Range(1, 80).Select(i => $"token{i}"));

        var chunks = chunker.Chunk(
            Guid.NewGuid(),
            "sample.txt",
            "objects/sample.txt",
            new ExtractedDocument([new ExtractedPage(null, text)]));

        Assert.Equal(3, chunks.Count);
        Assert.StartsWith("token1 token2", chunks[0].Text);
        Assert.StartsWith("token26 token27", chunks[1].Text);
        Assert.StartsWith("token51 token52", chunks[2].Text);
    }

    [Fact]
    public void ApproximateTokenEstimator_SplitsOnWhitespace()
    {
        var estimator = new ApproximateTokenEstimator();

        var tokens = estimator.EstimateTokens("one  two\tthree\nfour");

        Assert.Equal(["one", "two", "three", "four"], tokens);
    }

    [Fact]
    public void Chunk_UsesInjectedTokenEstimator()
    {
        var options = Options.Create(new RagOptions
        {
            Ingestion = new IngestionOptions
            {
                ChunkTokenCount = 50,
                ChunkOverlapTokens = 0
            }
        });
        var chunker = new TokenTextChunker(options, new FixedTokenEstimator(["alpha", "beta", "gamma"]));

        var chunks = chunker.Chunk(
            Guid.NewGuid(),
            "sample.txt",
            "objects/sample.txt",
            new ExtractedDocument([new ExtractedPage(null, "ignored by fake estimator")]));

        var chunk = Assert.Single(chunks);
        Assert.Equal("alpha beta gamma", chunk.Text);
    }

    private sealed class FixedTokenEstimator(IReadOnlyList<string> tokens) : ITokenEstimator
    {
        public IReadOnlyList<string> EstimateTokens(string text)
        {
            return tokens;
        }
    }
}
