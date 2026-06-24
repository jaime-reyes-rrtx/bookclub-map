using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class LiteraryArtifactGenerator(
    ILiteraryAnalysisProvider analysisProvider,
    IOptions<RagOptions> options,
    ILogger<LiteraryArtifactGenerator> logger) : ILiteraryArtifactGenerator
{
    private const string PromptVersion = "book-club-profile-v1";

    public LiteraryArtifactGenerator(
        ILiteraryAnalysisProvider analysisProvider,
        ILogger<LiteraryArtifactGenerator> logger)
        : this(analysisProvider, Options.Create(new RagOptions()), logger)
    {
    }

    public async Task<IReadOnlyList<TextChunk>> GenerateArtifactsAsync(
        Guid documentId,
        string fileName,
        string objectKey,
        ExtractedDocument document,
        IReadOnlyList<TextChunk> sourceChunks,
        CancellationToken cancellationToken)
    {
        var candidateNames = ExtractLikelyNames(document).Take(40).ToList();
        var excerpts = SelectRepresentativeExcerpts(sourceChunks);
        var generatedAtUtc = DateTimeOffset.UtcNow;
        var sourceChunkIndexes = sourceChunks.Select(chunk => chunk.ChunkIndex).Distinct().Order().ToArray();
        var sourcePageNumbers = sourceChunks
            .Select(chunk => chunk.PageNumber)
            .Where(page => page.HasValue)
            .Select(page => page!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var artifacts = new List<TextChunk>
        {
            CreateArtifact(
                documentId,
                fileName,
                objectKey,
                -1,
                "literary_name_profile",
                "Literary name profile",
                BuildNameProfile(fileName, candidateNames),
                new ChunkProvenance(
                    IsGenerated: true,
                    ArtifactKind: "name-profile",
                    Provider: "RAGPipeline",
                    Model: "deterministic-name-extractor",
                    PromptVersion: "deterministic-name-profile-v1",
                    GeneratedAtUtc: generatedAtUtc,
                    SourceChunkIndexes: sourceChunkIndexes,
                    SourcePageNumbers: sourcePageNumbers))
        };

        try
        {
            var profile = await analysisProvider.GenerateBookClubProfileAsync(
                fileName,
                candidateNames,
                excerpts,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(profile))
            {
                artifacts.Add(CreateArtifact(
                    documentId,
                    fileName,
                    objectKey,
                    -2,
                    "literary_book_club_profile",
                    "Book club literary profile",
                    profile,
                    new ChunkProvenance(
                        IsGenerated: true,
                        ArtifactKind: "book-club-profile",
                        Provider: options.Value.Ai.Provider,
                        Model: options.Value.Ai.ChatModel,
                        PromptVersion: PromptVersion,
                        GeneratedAtUtc: generatedAtUtc,
                        SourceChunkIndexes: sourceChunkIndexes,
                        SourcePageNumbers: sourcePageNumbers)));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not generate LLM literary profile for {FileName}. Continuing with deterministic profile.", fileName);
        }

        return artifacts;
    }

    private static TextChunk CreateArtifact(
        Guid documentId,
        string fileName,
        string objectKey,
        int chunkIndex,
        string chunkType,
        string title,
        string text,
        ChunkProvenance provenance)
    {
        return new TextChunk(
            Guid.NewGuid(),
            documentId,
            fileName,
            objectKey,
            chunkIndex,
            null,
            text,
            chunkType,
            title,
            provenance);
    }

    private static string BuildNameProfile(string fileName, IReadOnlyList<string> candidateNames)
    {
        var names = candidateNames.Count == 0 ? "No strong recurring names detected." : string.Join(", ", candidateNames);

        return $"""
            Literary artifact: name and entity profile
            Document: {fileName}
            Artifact type: literary_name_profile
            Purpose: Help answer book-club and literary-analysis questions about protagonists, main characters, recurring figures, cast, relationships, themes, and summaries.
            Frequently mentioned names and character-like entities: {names}.
            Use this artifact as a signal, not final proof. Prefer the book-club literary profile when it identifies protagonists, character roles, themes, or interpretive claims.
            """;
    }

    private static IReadOnlyList<string> SelectRepresentativeExcerpts(IReadOnlyList<TextChunk> sourceChunks)
    {
        if (sourceChunks.Count == 0)
        {
            return [];
        }

        var indexes = new SortedSet<int>
        {
            0,
            Math.Min(1, sourceChunks.Count - 1),
            Math.Max(0, sourceChunks.Count / 2),
            sourceChunks.Count - 1
        };

        return indexes
            .Select(index => sourceChunks[index].Text)
            .Select(text => text.Length <= 900 ? text : text[..900])
            .ToList();
    }

    private static IEnumerable<string> ExtractLikelyNames(ExtractedDocument document)
    {
        var text = string.Join(' ', document.Pages.Select(page => page.Text));
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "The", "A", "An", "And", "But", "Or", "If", "As", "At", "In", "On", "Of", "For", "To", "From",
            "By", "With", "Chapter", "Page", "Book", "Books", "Illustrated", "Edition", "Published",
            "Publishing", "Copyright", "London", "Bloomsbury", "Contents", "Praise", "Introduction"
        };

        return Regex.Matches(text, @"\b[A-Z][a-zA-Z']{2,}(?:\s+[A-Z][a-zA-Z']{2,})?\b")
            .Select(match => NormalizeName(match.Value))
            .Where(name => name.Length > 2)
            .Where(name => !ignored.Contains(name))
            .Where(name => name.Split(' ').All(part => !ignored.Contains(part)))
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key);
    }

    private static string NormalizeName(string name)
    {
        return string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
