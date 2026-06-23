using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class GeminiEmbeddingProvider(HttpClient httpClient, IOptions<RagOptions> options) : IEmbeddingProvider
{
    private readonly RagOptions _options = options.Value;

    public async Task<float[]> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken)
    {
        var ai = _options.Ai;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{ai.EmbeddingModel}:embedContent")
        {
            Content = JsonContent.Create(new GeminiEmbeddingRequest(
                new GeminiContent([new GeminiPart(input)]),
                _options.Qdrant.VectorSize))
        };

        request.Headers.Add("x-goog-api-key", ResolveApiKey(ai));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GeminiEmbeddingResponse>(cancellationToken);
        var values = result?.Embedding?.Values ?? result?.Embeddings?.FirstOrDefault()?.Values;
        if (values is not { Length: > 0 })
        {
            throw new InvalidOperationException("Gemini returned an empty embedding.");
        }

        return values;
    }

    private static string ResolveApiKey(AiOptions options)
    {
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
            ? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            : options.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini provider requires GEMINI_API_KEY or Rag:Ai:ApiKey.");
        }

        return apiKey;
    }
}

public sealed class GeminiChatCompletionProvider(HttpClient httpClient, IOptions<RagOptions> options) : IChatCompletionProvider
{
    private readonly AiOptions _options = options.Value.Ai;

    public Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken cancellationToken)
    {
        return GenerateContentAsync(BuildSystemPrompt(), BuildUserPrompt(question, chunks), cancellationToken);
    }

    internal async Task<string> GenerateContentAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"models/{_options.ChatModel}:generateContent")
        {
            Content = JsonContent.Create(new GeminiGenerateContentRequest(
                new GeminiContent([new GeminiPart(systemPrompt)]),
                [new GeminiContent([new GeminiPart(userPrompt)])]))
        };

        request.Headers.Add("x-goog-api-key", ResolveApiKey(_options));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GeminiGenerateContentResponse>(cancellationToken);
        var text = result?.Candidates?
            .SelectMany(candidate => candidate.Content?.Parts ?? [])
            .Select(part => part.Text)
            .FirstOrDefault(partText => !string.IsNullOrWhiteSpace(partText));

        return string.IsNullOrWhiteSpace(text)
            ? "No answer was returned by the configured chat provider."
            : text.Trim();
    }

    private static string ResolveApiKey(AiOptions options)
    {
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey)
            ? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            : options.ApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Gemini provider requires GEMINI_API_KEY or Rag:Ai:ApiKey.");
        }

        return apiKey;
    }

    private static string BuildSystemPrompt()
    {
        return """
            You are a book-club literary expert and document question-answering assistant.
            Answer the user's question directly and concisely from the provided excerpts.
            Prefer literary artifact excerpts for broad questions about protagonists, main characters, themes, summaries, interpretation, motifs, relationships, or discussion.
            Do not copy, rewrite, or dump long passages from the excerpts.
            Start with the answer, not with phrases like "Here is the text" or "The passage says".
            For "name" or "who" questions, provide the names first, then a short explanation if useful.
            For opinion or interpretation questions, distinguish textual evidence from interpretation.
            Cite supporting excerpts inline with bracket numbers like [1] or [2].
            If the excerpts are insufficient, say what is missing instead of inventing facts.
            """;
    }

    private static string BuildUserPrompt(string question, IReadOnlyList<RetrievedChunk> chunks)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Question:");
        builder.AppendLine(question);
        builder.AppendLine();
        builder.AppendLine("Excerpts available for evidence:");

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var page = chunk.PageNumber is null ? "" : $", page {chunk.PageNumber}";
            builder.AppendLine($"[{i + 1}] {chunk.FileName}{page}, chunk {chunk.ChunkIndex}");
            builder.AppendLine(chunk.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }
}

public sealed class GeminiLiteraryAnalysisProvider(GeminiChatCompletionProvider chat) : ILiteraryAnalysisProvider
{
    public async Task<string> GenerateBookClubProfileAsync(
        string fileName,
        IReadOnlyList<string> candidateNames,
        IReadOnlyList<string> excerpts,
        CancellationToken cancellationToken)
    {
        var profile = await chat.GenerateContentAsync(
            BuildLiterarySystemPrompt(),
            BuildLiteraryUserPrompt(fileName, candidateNames, excerpts),
            cancellationToken);

        return string.IsNullOrWhiteSpace(profile) || profile.StartsWith("No answer was returned", StringComparison.OrdinalIgnoreCase)
            ? ""
            : NormalizeProfile(profile, fileName);
    }

    private static string BuildLiterarySystemPrompt()
    {
        return """
            You are a literary analyst preparing searchable book-club notes for a RAG system.
            Create concise, structured notes from the provided excerpts and candidate names.
            Do not continue the story. Do not invent plot events. If something is uncertain, write "unclear from excerpts".
            Use this exact section format:
            Title:
            Likely protagonists:
            Major characters:
            Setting:
            Plot overview:
            Character arcs:
            Themes:
            Motifs and symbols:
            Book club discussion questions:
            Evidence notes:
            """;
    }

    private static string BuildLiteraryUserPrompt(
        string fileName,
        IReadOnlyList<string> candidateNames,
        IReadOnlyList<string> excerpts)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Document: {fileName}");
        builder.AppendLine("Candidate recurring names:");
        builder.AppendLine(candidateNames.Count == 0 ? "None detected." : string.Join(", ", candidateNames));
        builder.AppendLine();
        builder.AppendLine("Representative excerpts:");

        for (var i = 0; i < excerpts.Count; i++)
        {
            builder.AppendLine($"[Excerpt {i + 1}]");
            builder.AppendLine(excerpts[i]);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string NormalizeProfile(string profile, string fileName)
    {
        return $"""
            Literary artifact: book-club profile
            Document: {fileName}
            Artifact type: literary_book_club_profile
            Use: Prefer this artifact for broad literary questions about protagonists, main characters, themes, summaries, interpretation, relationships, motifs, and book-club discussion.

            {profile}
            """;
    }
}

internal sealed record GeminiEmbeddingRequest(
    [property: JsonPropertyName("content")] GeminiContent Content,
    [property: JsonPropertyName("output_dimensionality")] int OutputDimensionality);

internal sealed record GeminiEmbeddingResponse(
    [property: JsonPropertyName("embedding")] GeminiEmbedding? Embedding,
    [property: JsonPropertyName("embeddings")] IReadOnlyList<GeminiEmbedding>? Embeddings);

internal sealed record GeminiEmbedding(
    [property: JsonPropertyName("values")] float[] Values);

internal sealed record GeminiGenerateContentRequest(
    [property: JsonPropertyName("system_instruction")] GeminiContent SystemInstruction,
    [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents);

internal sealed record GeminiGenerateContentResponse(
    [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates);

internal sealed record GeminiCandidate(
    [property: JsonPropertyName("content")] GeminiContent? Content);

internal sealed record GeminiContent(
    [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts);

internal sealed record GeminiPart(
    [property: JsonPropertyName("text")] string Text);
