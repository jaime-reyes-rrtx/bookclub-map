using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;
using System.Text.RegularExpressions;

namespace RAG.Core.Services;

public sealed class ChatAnswerService(
    IEmbeddingProvider embeddings,
    IVectorStore vectorStore,
    IChatCompletionProvider chat,
    IRetrievalReranker? reranker = null,
    IOptions<RagOptions>? options = null,
    ILogger<ChatAnswerService>? logger = null) : IChatAnswerService
{
    private const int CandidateLimitPerQuery = 8;
    private const int MaxContextChunks = 16;
    private const int MaxCitations = MaxContextChunks;
    private const double ComparisonMinimumRank = 0.9;
    private readonly RequestOptions _requestOptions = options?.Value.Request ?? new RequestOptions();
    private readonly IRetrievalReranker _reranker = reranker ?? new HeuristicRetrievalReranker();
    private readonly ILogger<ChatAnswerService> _logger = logger ?? NullLogger<ChatAnswerService>.Instance;

    public ChatAnswerService(
        IEmbeddingProvider embeddings,
        IVectorStore vectorStore,
        IChatCompletionProvider chat,
        IOptions<RagOptions> options)
        : this(embeddings, vectorStore, chat, null, options, null)
    {
    }

    public async Task<AskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        using var providerTimeout = CreateProviderTimeout(cancellationToken);
        var retrieval = await RetrieveCandidateChunksAsync(request, providerTimeout.Token);
        var chunks = retrieval.SelectedChunks;
        _logger.LogInformation(
            "Retrieved {QueryCount} query result set(s), {CandidateCount} candidate chunk(s), and {SelectedContextCount} selected context chunk(s).",
            retrieval.Diagnostics.Queries.Count,
            retrieval.Diagnostics.Candidates.Count,
            retrieval.Diagnostics.SelectedContext.Count);

        if (chunks.Count == 0)
        {
            return new AskResponse(
                "No indexed document chunks matched the question.",
                [],
                request.IncludeDiagnostics ? retrieval.Diagnostics : null);
        }

        var contextChunks = chunks.Take(MaxContextChunks).ToList();
        if (TryAnswerBroadCharacterQuestion(request.Question, contextChunks, out var profileAnswer))
        {
            return new AskResponse(
                profileAnswer,
                BuildCitations(contextChunks),
                request.IncludeDiagnostics ? retrieval.Diagnostics : null);
        }

        var providerLatency = Stopwatch.StartNew();
        string answer;
        try
        {
            answer = await chat.GenerateAnswerAsync(request.Question, contextChunks, providerTimeout.Token);
            providerLatency.Stop();
            _logger.LogInformation(
                "Chat provider completed in {ProviderLatencyMs} ms for {SelectedContextCount} selected context chunk(s).",
                providerLatency.ElapsedMilliseconds,
                contextChunks.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            providerLatency.Stop();
            _logger.LogWarning(
                ex,
                "Chat provider failed after {ProviderLatencyMs} ms for {SelectedContextCount} selected context chunk(s).",
                providerLatency.ElapsedMilliseconds,
                contextChunks.Count);
            throw;
        }

        return new AskResponse(
            answer,
            BuildCitations(contextChunks),
            request.IncludeDiagnostics ? retrieval.Diagnostics : null);
    }

    private void ValidateRequest(AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            throw new ArgumentException("Question is required.", nameof(request));
        }

        if (request.Question.Length > _requestOptions.MaxQuestionCharacters)
        {
            throw new ArgumentException($"Question must be {_requestOptions.MaxQuestionCharacters} characters or fewer.", nameof(request));
        }

        if (request.DocumentIds is { Length: > 0 } documentIds &&
            documentIds.Distinct().Count() > _requestOptions.MaxSelectedDocuments)
        {
            throw new ArgumentException($"Select {_requestOptions.MaxSelectedDocuments} documents or fewer.", nameof(request));
        }
    }

    private CancellationTokenSource CreateProviderTimeout(CancellationToken cancellationToken)
    {
        var timeoutSeconds = Math.Max(1, _requestOptions.ProviderTimeoutSeconds);
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return source;
    }

    private static IReadOnlyList<CitationDto> BuildCitations(IReadOnlyList<RetrievedChunk> chunks)
    {
        return chunks.Take(MaxCitations).Select(chunk => new CitationDto(
            chunk.DocumentId,
            chunk.FileName,
            chunk.ChunkIndex,
            chunk.PageNumber,
            chunk.Score,
            ToSnippet(chunk.Text),
            chunk.ChunkType,
            string.IsNullOrWhiteSpace(chunk.Title) ? null : chunk.Title,
            chunk.Provenance?.IsGenerated ?? false,
            chunk.Provenance?.ArtifactKind)).ToList();
    }

    private async Task<RetrievalResult> RetrieveCandidateChunksAsync(
        AskRequest request,
        CancellationToken cancellationToken)
    {
        var question = request.Question.Trim();
        var isComparisonQuestion = LooksLikeComparisonQuestion(question);
        var namedSubjects = ExtractNamedSubjects(question);
        var context = new RetrievalContext(question, isComparisonQuestion, namedSubjects);
        var candidates = new Dictionary<string, RankedChunk>(StringComparer.Ordinal);
        var queries = BuildSearchQueries(question)
            .Take(Math.Max(1, _requestOptions.MaxRetrievalQueries))
            .ToList();

        foreach (var query in queries)
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
                var ranked = _reranker.Rerank(chunk, query, context);
                if (!candidates.TryGetValue(key, out var existing) || ranked.Rank > existing.Rank)
                {
                    candidates[key] = ranked;
                }
            }
        }

        if (isComparisonQuestion && namedSubjects.Count > 0)
        {
            var profileChunks = await vectorStore.GetDocumentProfileChunksAsync(request.DocumentIds, cancellationToken);
            foreach (var chunk in profileChunks.Where(chunk => HeuristicRetrievalReranker.ContainsAnyNamedSubject(chunk, namedSubjects)))
            {
                var key = $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
                var matchingNameCount = namedSubjects.Count(subject => HeuristicRetrievalReranker.ContainsPhrase(chunk, subject));
                var rank = 1.2 + matchingNameCount * 0.35;
                var ranked = new RankedChunk(
                    chunk,
                    rank,
                    ["matched generated profile for named subject", $"matched {matchingNameCount} named subject(s)"]);
                if (!candidates.TryGetValue(key, out var existing) || rank > existing.Rank)
                {
                    candidates[key] = ranked;
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
                var matchingNameCount = namedSubjects.Count(subject => HeuristicRetrievalReranker.ContainsPhrase(chunk, subject));
                var isProfile = HeuristicRetrievalReranker.IsProfileChunk(chunk);
                var rank = 0.95 + matchingNameCount * 0.25 + (isProfile ? 0.2 : 0);
                var ranked = new RankedChunk(
                    chunk,
                    rank,
                    isProfile
                        ? ["exact named-subject match", "generated profile boost"]
                        : ["exact named-subject match"]);
                if (!candidates.TryGetValue(key, out var existing) || rank > existing.Rank)
                {
                    candidates[key] = ranked;
                }
            }
        }

        var selected = SelectContextChunks(candidates.Values, isComparisonQuestion);
        return new RetrievalResult(
            selected.Select(candidate => candidate.Chunk).ToList(),
            BuildDiagnostics(question, queries, candidates.Values, selected, isComparisonQuestion, namedSubjects));
    }

    private static IReadOnlyList<RetrievalQuery> BuildSearchQueries(string question)
    {
        var normalized = question.Trim();
        var queries = new List<RetrievalQuery> { new(normalized) };

        if (LooksLikeBroadDocumentQuestion(normalized) || LooksLikeComparisonQuestion(normalized))
        {
            queries.Add(new RetrievalQuery($"literary book club profile protagonists major characters themes interpretation summary: {normalized}"));
            queries.Add(new RetrievalQuery($"main characters protagonists central people important names relationships overview summary: {normalized}"));
            queries.Add(new RetrievalQuery($"who are the key people and what roles do they have: {normalized}"));
        }

        if (LooksLikeComparisonQuestion(normalized))
        {
            queries.Add(new RetrievalQuery($"compare similarities differences character traits motivations relationships themes: {normalized}"));
        }

        foreach (var entity in ExtractNamedSubjects(normalized))
        {
            queries.Add(new RetrievalQuery($"literary name profile {entity} major characters relationships traits", entity));
            queries.Add(new RetrievalQuery($"book club profile {entity} character motivations themes similarities differences", entity));
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

    private static IReadOnlyList<RankedChunk> SelectContextChunks(
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

    private static RetrievalDiagnostics BuildDiagnostics(
        string question,
        IReadOnlyList<RetrievalQuery> queries,
        IEnumerable<RankedChunk> candidates,
        IReadOnlyList<RankedChunk> selected,
        bool isComparisonQuestion,
        IReadOnlyList<string> namedSubjects)
    {
        var selectedKeys = selected.Select(candidate => BuildChunkKey(candidate.Chunk)).ToHashSet(StringComparer.Ordinal);

        return new RetrievalDiagnostics(
            question,
            queries.Select(query => new RetrievalQueryDiagnostic(query.Text, query.NamedSubject)).ToList(),
            candidates
                .OrderByDescending(candidate => candidate.Rank)
                .Select(candidate => new RetrievedCandidateDiagnostic(
                    candidate.Chunk.DocumentId,
                    candidate.Chunk.FileName,
                    candidate.Chunk.ChunkIndex,
                    candidate.Chunk.ChunkType,
                    string.IsNullOrWhiteSpace(candidate.Chunk.Title) ? null : candidate.Chunk.Title,
                    candidate.Chunk.Score,
                    candidate.Rank,
                    BuildDiagnosticRankReasons(candidate, selectedKeys, isComparisonQuestion),
                    selectedKeys.Contains(BuildChunkKey(candidate.Chunk))))
                .ToList(),
            selected
                .OrderByDescending(candidate => candidate.Rank)
                .Select(candidate => new SelectedContextDiagnostic(
                    candidate.Chunk.DocumentId,
                    candidate.Chunk.FileName,
                    candidate.Chunk.ChunkIndex,
                    candidate.Chunk.ChunkType,
                    string.IsNullOrWhiteSpace(candidate.Chunk.Title) ? null : candidate.Chunk.Title,
                    candidate.Rank,
                    ToSnippet(candidate.Chunk.Text)))
                .ToList(),
            isComparisonQuestion,
            namedSubjects);
    }

    private static IReadOnlyList<string> BuildDiagnosticRankReasons(
        RankedChunk candidate,
        HashSet<string> selectedKeys,
        bool isComparisonQuestion)
    {
        var reasons = candidate.RankReasons.ToList();
        if (isComparisonQuestion && candidate.Rank < ComparisonMinimumRank)
        {
            reasons.Add("filtered below comparison rank threshold");
        }
        else if (!selectedKeys.Contains(BuildChunkKey(candidate.Chunk)))
        {
            reasons.Add("not selected after context limit and deduplication");
        }

        return reasons;
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

    private static string BuildChunkKey(RetrievedChunk chunk)
    {
        return $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
    }

    private sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> SelectedChunks, RetrievalDiagnostics Diagnostics);

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
        "Compare",
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
