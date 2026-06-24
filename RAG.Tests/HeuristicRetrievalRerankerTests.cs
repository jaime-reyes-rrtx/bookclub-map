using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class HeuristicRetrievalRerankerTests
{
    [Fact]
    public void Rerank_BoostsGeneratedProfilesForBroadQuestions()
    {
        var reranker = new HeuristicRetrievalReranker();
        var chunk = new RetrievedChunk(
            Guid.NewGuid(),
            "novel.txt",
            -2,
            null,
            "objects/novel.txt",
            "Likely protagonists: Alice Morgan.",
            .5,
            "literary_book_club_profile",
            "Book club literary profile");

        var ranked = reranker.Rerank(
            chunk,
            new RetrievalQuery("Who are the protagonists?"),
            new RetrievalContext("Who are the protagonists?", IsComparisonQuestion: false, []));

        Assert.True(ranked.Rank > chunk.Score);
        Assert.Contains("generated profile boost", ranked.RankReasons);
    }

    [Fact]
    public void Rerank_BoostsNamedSubjectMatches()
    {
        var reranker = new HeuristicRetrievalReranker();
        var chunk = new RetrievedChunk(
            Guid.NewGuid(),
            "novel.txt",
            3,
            10,
            "objects/novel.txt",
            "Alice Morgan solves the library mystery.",
            .5);

        var ranked = reranker.Rerank(
            chunk,
            new RetrievalQuery("literary name profile Alice Morgan", "Alice Morgan"),
            new RetrievalContext("Compare Alice Morgan and Bruno Stone.", IsComparisonQuestion: true, ["Alice Morgan", "Bruno Stone"]));

        Assert.True(ranked.Rank > chunk.Score);
        Assert.Contains("named-subject query match", ranked.RankReasons);
        Assert.Contains("comparison named-subject match", ranked.RankReasons);
    }
}
