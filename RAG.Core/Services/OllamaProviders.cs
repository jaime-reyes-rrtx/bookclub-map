using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class OllamaEmbeddingProvider(HttpClient httpClient, IOptions<RagOptions> options) : IEmbeddingProvider
{
    private readonly AiOptions _options = options.Value.Ai;

    public async Task<float[]> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/embeddings",
            new OllamaEmbeddingRequest(_options.EmbeddingModel, input),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken);
        if (result?.Embedding is not { Length: > 0 })
        {
            throw new InvalidOperationException("Ollama returned an empty embedding.");
        }

        return result.Embedding;
    }
}

public sealed class OllamaChatCompletionProvider(HttpClient httpClient, IOptions<RagOptions> options) : IChatCompletionProvider
{
    private readonly AiOptions _options = options.Value.Ai;

    public async Task<string> GenerateAnswerAsync(
        string question,
        IReadOnlyList<RetrievedChunk> chunks,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/chat",
            new OllamaChatRequest(
                _options.ChatModel,
                [
                    new OllamaChatMessage("system", BuildSystemPrompt()),
                    new OllamaChatMessage("user", BuildUserPrompt(question, chunks))
                ],
                Stream: false),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);
        return string.IsNullOrWhiteSpace(result?.Message?.Content)
            ? "No answer was returned by the configured chat provider."
            : result.Message.Content.Trim();
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

public sealed class OllamaLiteraryAnalysisProvider(HttpClient httpClient, IOptions<RagOptions> options) : ILiteraryAnalysisProvider
{
    private readonly AiOptions _options = options.Value.Ai;

    public async Task<string> GenerateBookClubProfileAsync(
        string fileName,
        IReadOnlyList<string> candidateNames,
        IReadOnlyList<string> excerpts,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/api/chat",
            new OllamaChatRequest(
                _options.ChatModel,
                [
                    new OllamaChatMessage("system", BuildLiterarySystemPrompt()),
                    new OllamaChatMessage("user", BuildLiteraryUserPrompt(fileName, candidateNames, excerpts))
                ],
                Stream: false),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken);
        return string.IsNullOrWhiteSpace(result?.Message?.Content)
            ? ""
            : NormalizeProfile(result.Message.Content.Trim(), fileName);
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

internal sealed record OllamaEmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt")] string Prompt);

internal sealed record OllamaEmbeddingResponse(
    [property: JsonPropertyName("embedding")] float[] Embedding);

internal sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed record OllamaChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OllamaChatResponse(
    [property: JsonPropertyName("message")] OllamaChatMessage? Message);
