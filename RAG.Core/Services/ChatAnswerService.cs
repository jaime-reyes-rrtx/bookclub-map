using RAG.Core.Models;
using System.Text.RegularExpressions;

namespace RAG.Core.Services;

public sealed class ChatAnswerService(
    IEmbeddingProvider embeddings,
    IVectorStore vectorStore,
    IChatCompletionProvider chat) : IChatAnswerService
{
    private const int CandidateLimitPerQuery = 8;
    private const int MaxContextChunks = 16;
    private const int MaxCitations = MaxContextChunks;
    private const double ComparisonMinimumRank = 0.9;

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw new ArgumentException("Question is required.", nameof(request));
        }

        var chunks = await RetrieveCandidateChunksAsync(request, cancellationToken);

        if (chunks.Count == 0)
        {
            return new AskResponse("No indexed document chunks matched the question.", []);
        }

        var contextChunks = chunks.Take(MaxContextChunks).ToList();
        if (TryAnswerBroadCharacterQuestion(request.Question, contextChunks, out var profileAnswer))
        {
            return new AskResponse(profileAnswer, BuildCitations(contextChunks));
        }

        var answer = await chat.GenerateAnswerAsync(request.Question, contextChunks, cancellationToken);
        return new AskResponse(answer, BuildCitations(contextChunks));
    }

    private static IReadOnlyList<CitationDto> BuildCitations(IReadOnlyList<RetrievedChunk> chunks)
    {
        return chunks.Take(MaxCitations).Select(chunk => new CitationDto(
            chunk.DocumentId,
            chunk.FileName,
            chunk.ChunkIndex,
            chunk.PageNumber,
            chunk.Score,
            ToSnippet(chunk.Text))).ToList();
    }

    private async Task<IReadOnlyList<RetrievedChunk>> RetrieveCandidateChunksAsync(
        AskRequest request,
        CancellationToken cancellationToken)
    {
        var question = request.Question.Trim();
        var isComparisonQuestion = LooksLikeComparisonQuestion(question);
        var namedSubjects = ExtractNamedSubjects(question);
        var candidates = new Dictionary<string, RankedChunk>(StringComparer.Ordinal);

        foreach (var query in BuildSearchQueries(question))
        {
            var embedding = await embeddings.GenerateEmbeddingAsync(query.Text, cancellationToken);
            var chunks = await vectorStore.SearchAsync(
                embedding,
                CandidateLimitPerQuery,
                request.DocumentIds,
                cancellationToken);

            foreach (var chunk in chunks)
            {
                var key = $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
                var rank = RankChunk(chunk, question, query, isComparisonQuestion);
                if (!candidates.TryGetValue(key, out var existing) || rank > existing.Rank)
                {
                    candidates[key] = new RankedChunk(chunk, rank);
                }
            }
        }

        if (isComparisonQuestion && namedSubjects.Count > 0)
        {
            var profileChunks = await vectorStore.GetDocumentProfileChunksAsync(request.DocumentIds, cancellationToken);
            foreach (var chunk in profileChunks.Where(chunk => ContainsAnyNamedSubject(chunk, namedSubjects)))
            {
                var key = $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
                var matchingNameCount = namedSubjects.Count(subject => ContainsPhrase(chunk, subject));
                var rank = 1.2 + matchingNameCount * 0.35;
                if (!candidates.TryGetValue(key, out var existing) || rank > existing.Rank)
                {
                    candidates[key] = new RankedChunk(chunk, rank);
                }
            }

            var exactNameChunks = await vectorStore.GetChunksContainingTextAsync(
                namedSubjects,
                request.DocumentIds,
                limitPerTerm: 4,
                cancellationToken);
            foreach (var chunk in exactNameChunks)
            {
                var key = $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
                var matchingNameCount = namedSubjects.Count(subject => ContainsPhrase(chunk, subject));
                var isProfile = chunk.ChunkIndex < 0 || chunk.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase);
                var rank = 0.95 + matchingNameCount * 0.25 + (isProfile ? 0.2 : 0);
                if (!candidates.TryGetValue(key, out var existing) || rank > existing.Rank)
                {
                    candidates[key] = new RankedChunk(chunk, rank);
                }
            }
        }

        return SelectContextChunks(candidates.Values, isComparisonQuestion);
    }

    private static IReadOnlyList<SearchQuery> BuildSearchQueries(string question)
    {
        var normalized = question.Trim();
        var queries = new List<SearchQuery> { new(normalized) };

        if (LooksLikeBroadDocumentQuestion(normalized) || LooksLikeComparisonQuestion(normalized))
        {
            queries.Add(new SearchQuery($"literary book club profile protagonists major characters themes interpretation summary: {normalized}"));
            queries.Add(new SearchQuery($"main characters protagonists central people important names relationships overview summary: {normalized}"));
            queries.Add(new SearchQuery($"who are the key people and what roles do they have: {normalized}"));
        }

        if (LooksLikeComparisonQuestion(normalized))
        {
            queries.Add(new SearchQuery($"compare similarities differences character traits motivations relationships themes: {normalized}"));
        }

        foreach (var entity in ExtractNamedSubjects(normalized))
        {
            queries.Add(new SearchQuery($"literary name profile {entity} major characters relationships traits", entity));
            queries.Add(new SearchQuery($"book club profile {entity} character motivations themes similarities differences", entity));
        }

        return queries;
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

    private static bool LooksLikeComparisonQuestion(string question)
    {
        var lower = question.ToLowerInvariant();
        string[] comparisonTerms =
        [
            "similar",
            "similarities",
            "difference",
            "differences",
            "compare",
            "comparison",
            "contrast",
            "between",
            "both",
            "across",
            "versus",
            " vs ",
            "alike",
            "common"
        ];

        return comparisonTerms.Any(lower.Contains);
    }

    private static IReadOnlyList<string> ExtractNamedSubjects(string question)
    {
        return NamedSubjectPattern.Matches(question)
            .Select(match => match.Value.Trim())
            .Where(value => !IgnoredNamedSubjects.Contains(value))
            .Select(value => value.TrimEnd('.', ',', ';', ':', '?', '!'))
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static IReadOnlyList<RetrievedChunk> SelectContextChunks(
        IEnumerable<RankedChunk> candidates,
        bool isComparisonQuestion)
    {
        var ranked = candidates
            .OrderByDescending(candidate => candidate.Rank)
            .ToList();
        if (!isComparisonQuestion)
        {
            return ranked
                .Take(MaxContextChunks)
                .Select(candidate => candidate.Chunk)
                .ToList();
        }

        ranked = ranked
            .Where(candidate => candidate.Rank >= ComparisonMinimumRank)
            .ToList();
        var selected = new List<RankedChunk>();
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in ranked.GroupBy(candidate => candidate.Chunk.DocumentId))
        {
            var bestProfile = group
                .Where(candidate => candidate.Chunk.ChunkIndex < 0 || candidate.Chunk.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Rank)
                .FirstOrDefault();

            if (bestProfile is not null)
            {
                AddIfRoom(bestProfile);
            }
        }

        foreach (var candidate in ranked)
        {
            AddIfRoom(candidate);
        }

        return selected
            .OrderByDescending(candidate => candidate.Rank)
            .Take(MaxContextChunks)
            .Select(candidate => candidate.Chunk)
            .ToList();

        void AddIfRoom(RankedChunk candidate)
        {
            if (selected.Count >= MaxContextChunks)
            {
                return;
            }

            var key = $"{candidate.Chunk.DocumentId:N}:{candidate.Chunk.ChunkIndex}:{candidate.Chunk.ObjectKey}";
            if (selectedKeys.Add(key))
            {
                selected.Add(candidate);
            }
        }
    }

    private static double RankChunk(
        RetrievedChunk chunk,
        string question,
        SearchQuery query,
        bool isComparisonQuestion)
    {
        var rank = chunk.Score;
        var isProfile = chunk.ChunkIndex < 0 || chunk.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase);

        if (isProfile && (isComparisonQuestion || LooksLikeBroadDocumentQuestion(question)))
        {
            rank += 0.12;
        }

        if (!string.IsNullOrWhiteSpace(query.NamedSubject) && ContainsPhrase(chunk, query.NamedSubject))
        {
            rank += 0.35;
        }

        if (isComparisonQuestion && ContainsAnyNamedSubject(chunk, ExtractNamedSubjects(question)))
        {
            rank += 0.18;
        }

        return rank;
    }

    private static bool ContainsAnyNamedSubject(RetrievedChunk chunk, IReadOnlyList<string> namedSubjects)
    {
        return namedSubjects.Any(subject => ContainsPhrase(chunk, subject));
    }

    private static bool ContainsPhrase(RetrievedChunk chunk, string phrase)
    {
        return ContainsPhrase(chunk.Text, phrase) ||
               ContainsPhrase(chunk.FileName, phrase) ||
               ContainsPhrase(chunk.Title, phrase);
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        return text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryAnswerBroadCharacterQuestion(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        out string answer)
    {
        answer = "";
        var lower = question.ToLowerInvariant();
        if (!lower.Contains("protagonist") &&
            !(lower.Contains("main") && lower.Contains("character")) &&
            !(lower.Contains("name") && lower.Contains("character")))
        {
            return false;
        }

        var profileChunk = chunks.FirstOrDefault(chunk => chunk.ChunkType == "literary_book_club_profile")
            ?? chunks.FirstOrDefault(chunk => chunk.ChunkType == "literary_name_profile")
            ?? chunks.FirstOrDefault(chunk => chunk.ChunkIndex < 0);
        if (profileChunk is null)
        {
            return false;
        }

        var names = ExtractProfileList(profileChunk.Text, "Likely protagonists:");
        if (names.Count == 0)
        {
            names = ExtractProfileList(profileChunk.Text, "Major characters:");
        }

        if (names.Count == 0)
        {
            names = ExtractProfileList(profileChunk.Text, "Frequently mentioned names and character-like entities:");
        }

        names = names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        if (names.Count == 0)
        {
            return false;
        }

        if (names.Any(IsUncertainProfileValue))
        {
            return false;
        }

        answer = names.Count == 1
            ? $"The likely protagonist identified by the literary profile is {names[0]} [1]."
            : $"The likely protagonists or central characters identified by the literary profile are {JoinNames(names)} [1].";
        return true;
    }

    private static IReadOnlyList<string> ExtractProfileList(string profileText, string marker)
    {
        var markerIndex = profileText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return [];
        }

        var sectionStart = markerIndex + marker.Length;
        var sectionEnd = profileText.Length;
        foreach (var heading in KnownProfileHeadings.Where(heading => !heading.Equals(marker, StringComparison.OrdinalIgnoreCase)))
        {
            var headingIndex = profileText.IndexOf(heading, sectionStart, StringComparison.OrdinalIgnoreCase);
            if (headingIndex > sectionStart && headingIndex < sectionEnd)
            {
                sectionEnd = headingIndex;
            }
        }

        return SplitProfileValues(profileText[sectionStart..sectionEnd]);
    }

    private static IReadOnlyList<string> SplitProfileValues(string text)
    {
        return text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanProfileValue)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !name.Contains(':'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CleanProfileValue(string value)
    {
        var cleaned = value.Trim().TrimStart('-', '*').Trim();
        var parentheticalIndex = cleaned.IndexOf('(');
        if (parentheticalIndex > 0)
        {
            cleaned = cleaned[..parentheticalIndex].Trim();
        }

        return cleaned.TrimEnd('.');
    }

    private static readonly string[] KnownProfileHeadings =
    [
        "Title:",
        "Likely protagonists:",
        "Major characters:",
        "Setting:",
        "Plot overview:",
        "Character arcs:",
        "Themes:",
        "Motifs and symbols:",
        "Book club discussion questions:",
        "Evidence notes:",
        "Frequently mentioned names and character-like entities:"
    ];

    private static bool IsUncertainProfileValue(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("unclear") || lower.Contains("unknown") || lower.Contains("none detected");
    }

    private static string JoinNames(IReadOnlyList<string> names)
    {
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))}, and {names[^1]}"
        };
    }

    private static string ToSnippet(string text)
    {
        const int maxLength = 360;
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private sealed record SearchQuery(string Text, string? NamedSubject = null);

    private sealed record RankedChunk(RetrievedChunk Chunk, double Rank);

    private static readonly Regex NamedSubjectPattern = new(
        @"\b[A-Z][a-zA-Z]*(?:\s+[A-Z][a-zA-Z]*){0,3}\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredNamedSubjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "A",
        "An",
        "And",
        "Are",
        "Can",
        "Could",
        "Do",
        "Does",
        "Find",
        "How",
        "I",
        "Is",
        "Tell",
        "The",
        "What",
        "When",
        "Where",
        "Which",
        "Who",
        "Why",
        "Would"
    };
}
