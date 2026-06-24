using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class HeuristicRetrievalReranker : IRetrievalReranker
{
    public RankedChunk Rerank(RetrievedChunk candidate, RetrievalQuery query, RetrievalContext context)
    {
        var rank = candidate.Score;
        var reasons = new List<string> { "vector score" };
        var isProfile = IsProfileChunk(candidate);

        if (isProfile && (context.IsComparisonQuestion || LooksLikeBroadDocumentQuestion(context.Question)))
        {
            rank += 0.12;
            reasons.Add("generated profile boost");
        }

        if (!string.IsNullOrWhiteSpace(query.NamedSubject) && ContainsPhrase(candidate, query.NamedSubject))
        {
            rank += 0.35;
            reasons.Add("named-subject query match");
        }

        if (context.IsComparisonQuestion && ContainsAnyNamedSubject(candidate, context.NamedSubjects))
        {
            rank += 0.18;
            reasons.Add("comparison named-subject match");
        }

        return new RankedChunk(candidate, rank, reasons);
    }

    public static bool IsProfileChunk(RetrievedChunk chunk)
    {
        return chunk.ChunkIndex < 0 || chunk.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContainsAnyNamedSubject(RetrievedChunk chunk, IReadOnlyList<string> namedSubjects)
    {
        return namedSubjects.Any(subject => ContainsPhrase(chunk, subject));
    }

    public static bool ContainsPhrase(RetrievedChunk chunk, string phrase)
    {
        return ContainsPhrase(chunk.Text, phrase) ||
               ContainsPhrase(chunk.FileName, phrase) ||
               ContainsPhrase(chunk.Title, phrase);
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        return text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBroadDocumentQuestion(string question)
    {
        var lower = question.ToLowerInvariant();
        string[] broadTerms =
        [
            "protagonist",
            "character",
            "main",
            "summary",
            "summarize",
            "theme",
            "themes",
            "who",
            "name",
            "names",
            "overview"
        ];

        return broadTerms.Any(lower.Contains);
    }
}
