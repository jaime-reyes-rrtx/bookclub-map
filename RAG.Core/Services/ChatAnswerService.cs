using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class ChatAnswerService(
    IEmbeddingProvider embeddings,
    IVectorStore vectorStore,
    IChatCompletionProvider chat) : IChatAnswerService
{
    private const int CandidateLimitPerQuery = 8;
    private const int MaxContextChunks = 12;
    private const int MaxCitations = 5;

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
        var candidates = new Dictionary<string, RetrievedChunk>(StringComparer.Ordinal);

        foreach (var query in BuildSearchQueries(request.Question))
        {
            var embedding = await embeddings.GenerateEmbeddingAsync(query, cancellationToken);
            var chunks = await vectorStore.SearchAsync(
                embedding,
                CandidateLimitPerQuery,
                request.DocumentIds,
                cancellationToken);

            foreach (var chunk in chunks)
            {
                var key = $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
                if (!candidates.TryGetValue(key, out var existing) || chunk.Score > existing.Score)
                {
                    candidates[key] = chunk;
                }
            }
        }

        return candidates.Values
            .OrderByDescending(chunk => chunk.Score)
            .Take(MaxContextChunks)
            .ToList();
    }

    private static IReadOnlyList<string> BuildSearchQueries(string question)
    {
        var normalized = question.Trim();
        var queries = new List<string> { normalized };

        if (LooksLikeBroadDocumentQuestion(normalized))
        {
            queries.Add($"literary book club profile protagonists major characters themes interpretation summary: {normalized}");
            queries.Add($"main characters protagonists central people important names relationships overview summary: {normalized}");
            queries.Add($"who are the key people and what roles do they have: {normalized}");
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
}
