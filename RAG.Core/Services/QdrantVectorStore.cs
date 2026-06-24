using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class QdrantVectorStore(HttpClient httpClient, IOptions<RagOptions> options) : IVectorStore
{
    private readonly QdrantOptions _options = options.Value.Qdrant;

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken)
    {
        using var get = await httpClient.GetAsync($"/collections/{_options.CollectionName}", cancellationToken);
        if (get.IsSuccessStatusCode)
        {
            return;
        }

        if (get.StatusCode != HttpStatusCode.NotFound)
        {
            get.EnsureSuccessStatusCode();
        }

        using var put = await httpClient.PutAsJsonAsync(
            $"/collections/{_options.CollectionName}",
            new QdrantCreateCollectionRequest(new QdrantVectorConfig(_options.VectorSize, "Cosine")),
            cancellationToken);

        put.EnsureSuccessStatusCode();
    }

    public async Task UpsertChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        await EnsureCollectionAsync(cancellationToken);
        var points = chunks.Select(chunk => new QdrantPoint(
            chunk.Chunk.Id,
            chunk.Embedding,
            new Dictionary<string, object?>
            {
                ["documentId"] = chunk.Chunk.DocumentId.ToString(),
                ["fileName"] = chunk.Chunk.FileName,
                ["chunkIndex"] = chunk.Chunk.ChunkIndex,
                ["pageNumber"] = chunk.Chunk.PageNumber,
                ["sourceObjectKey"] = chunk.Chunk.ObjectKey,
                ["text"] = chunk.Chunk.Text,
                ["chunkType"] = chunk.Chunk.ChunkType,
                ["title"] = chunk.Chunk.Title,
                ["isGeneratedArtifact"] = chunk.Chunk.Provenance?.IsGenerated ?? false,
                ["artifactKind"] = chunk.Chunk.Provenance?.ArtifactKind,
                ["artifactProvider"] = chunk.Chunk.Provenance?.Provider,
                ["artifactModel"] = chunk.Chunk.Provenance?.Model,
                ["artifactPromptVersion"] = chunk.Chunk.Provenance?.PromptVersion,
                ["generatedAtUtc"] = chunk.Chunk.Provenance?.GeneratedAtUtc,
                ["sourceChunkIndexes"] = chunk.Chunk.Provenance?.SourceChunkIndexes ?? [],
                ["sourcePageNumbers"] = chunk.Chunk.Provenance?.SourcePageNumbers ?? []
            })).ToArray();

        using var response = await httpClient.PutAsJsonAsync(
            $"/collections/{_options.CollectionName}/points?wait=true",
            new QdrantUpsertRequest(points),
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await EnsureCollectionAsync(cancellationToken);

        using var response = await httpClient.PostAsJsonAsync(
            $"/collections/{_options.CollectionName}/points/delete?wait=true",
            new QdrantDeleteRequest(new QdrantFilter([
                new QdrantMust("documentId", new QdrantMatchAny([documentId.ToString()]))
            ])),
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] embedding,
        int limit,
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken)
    {
        await EnsureCollectionAsync(cancellationToken);

        var filter = documentIds is { Count: > 0 }
            ? new QdrantFilter([
                new QdrantMust(
                    "documentId",
                    new QdrantMatchAny(documentIds.Select(id => id.ToString()).ToArray()))
            ])
            : null;

        using var response = await httpClient.PostAsJsonAsync(
            $"/collections/{_options.CollectionName}/points/search",
            new QdrantSearchRequest(embedding, limit, WithPayload: true, filter),
            cancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>(cancellationToken);

        return result?.Result?
            .Select(ToRetrievedChunk)
            .Where(chunk => chunk is not null)
            .Cast<RetrievedChunk>()
            .ToList() ?? [];
    }

    public async Task<IReadOnlyList<RetrievedChunk>> GetDocumentProfileChunksAsync(
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken)
    {
        await EnsureCollectionAsync(cancellationToken);

        var filter = documentIds is { Count: > 0 }
            ? new QdrantFilter([
                new QdrantMust(
                    "documentId",
                    new QdrantMatchAny(documentIds.Select(id => id.ToString()).ToArray()))
            ])
            : null;
        var profileChunks = new List<RetrievedChunk>();
        JsonElement? offset = null;

        for (var page = 0; page < 20; page++)
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/collections/{_options.CollectionName}/points/scroll",
                new QdrantScrollRequest(filter, 256, WithPayload: true, WithVector: false, offset),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<QdrantScrollResponse>(cancellationToken);
            if (result?.Result?.Points is not { Count: > 0 } points)
            {
                break;
            }

            profileChunks.AddRange(points
                .Select(ToRetrievedChunk)
                .Where(chunk => chunk is { ChunkIndex: < 0 } || chunk?.ChunkType.StartsWith("literary_", StringComparison.OrdinalIgnoreCase) == true)
                .Cast<RetrievedChunk>());

            if (result.Result.NextPageOffset is null)
            {
                break;
            }

            offset = result.Result.NextPageOffset.Value.Clone();
        }

        return profileChunks;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> GetChunksContainingTextAsync(
        IReadOnlyCollection<string> terms,
        IReadOnlyCollection<Guid>? documentIds,
        int limitPerTerm,
        CancellationToken cancellationToken)
    {
        if (terms.Count == 0 || limitPerTerm <= 0)
        {
            return [];
        }

        var normalizedTerms = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTerms.Length == 0)
        {
            return [];
        }

        await EnsureCollectionAsync(cancellationToken);

        var filter = documentIds is { Count: > 0 }
            ? new QdrantFilter([
                new QdrantMust(
                    "documentId",
                    new QdrantMatchAny(documentIds.Select(id => id.ToString()).ToArray()))
            ])
            : null;
        var matches = new Dictionary<string, RetrievedChunk>(StringComparer.Ordinal);
        var termCounts = normalizedTerms.ToDictionary(term => term, _ => 0, StringComparer.OrdinalIgnoreCase);
        JsonElement? offset = null;

        for (var page = 0; page < 20 && termCounts.Values.Any(count => count < limitPerTerm); page++)
        {
            using var response = await httpClient.PostAsJsonAsync(
                $"/collections/{_options.CollectionName}/points/scroll",
                new QdrantScrollRequest(filter, 256, WithPayload: true, WithVector: false, offset),
                cancellationToken);

            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<QdrantScrollResponse>(cancellationToken);
            if (result?.Result?.Points is not { Count: > 0 } points)
            {
                break;
            }

            foreach (var chunk in points.Select(ToRetrievedChunk).Where(chunk => chunk is not null).Cast<RetrievedChunk>())
            {
                foreach (var term in normalizedTerms)
                {
                    if (termCounts[term] >= limitPerTerm || !ContainsPhrase(chunk, term))
                    {
                        continue;
                    }

                    var key = $"{chunk.DocumentId:N}:{chunk.ChunkIndex}:{chunk.ObjectKey}";
                    matches.TryAdd(key, chunk);
                    termCounts[term]++;
                    break;
                }
            }

            if (result.Result.NextPageOffset is null)
            {
                break;
            }

            offset = result.Result.NextPageOffset.Value.Clone();
        }

        return matches.Values.ToList();
    }

    private static RetrievedChunk? ToRetrievedChunk(QdrantSearchResult result)
    {
        return result.Payload is null ? null : ToRetrievedChunk(result.Payload, result.Score);
    }

    private static RetrievedChunk? ToRetrievedChunk(QdrantScrollPoint result)
    {
        return result.Payload is null ? null : ToRetrievedChunk(result.Payload, 0);
    }

    private static RetrievedChunk? ToRetrievedChunk(Dictionary<string, JsonElement> payload, double score)
    {
        var documentId = ReadString(payload, "documentId");
        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return null;
        }

        var provenance = new ChunkProvenance(
            IsGenerated: ReadBool(payload, "isGeneratedArtifact"),
            ArtifactKind: EmptyToNull(ReadString(payload, "artifactKind")),
            Provider: EmptyToNull(ReadString(payload, "artifactProvider")),
            Model: EmptyToNull(ReadString(payload, "artifactModel")),
            PromptVersion: EmptyToNull(ReadString(payload, "artifactPromptVersion")),
            GeneratedAtUtc: ReadDateTimeOffset(payload, "generatedAtUtc"),
            SourceChunkIndexes: ReadIntArray(payload, "sourceChunkIndexes"),
            SourcePageNumbers: ReadIntArray(payload, "sourcePageNumbers"));

        return new RetrievedChunk(
            parsedDocumentId,
            ReadString(payload, "fileName"),
            ReadInt(payload, "chunkIndex") ?? 0,
            ReadInt(payload, "pageNumber"),
            ReadString(payload, "sourceObjectKey"),
            ReadString(payload, "text"),
            score,
            ReadString(payload, "chunkType") is { Length: > 0 } chunkType ? chunkType : "source",
            ReadString(payload, "title"),
            provenance);
    }

    private static string ReadString(Dictionary<string, JsonElement> payload, string key)
    {
        return payload.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int? ReadInt(Dictionary<string, JsonElement> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static bool ReadBool(Dictionary<string, JsonElement> payload, string key)
    {
        return payload.TryGetValue(key, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
               value.GetBoolean();
    }

    private static DateTimeOffset? ReadDateTimeOffset(Dictionary<string, JsonElement> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static IReadOnlyList<int> ReadIntArray(Dictionary<string, JsonElement> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var parsed) ? parsed : (int?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .ToList();
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool ContainsPhrase(RetrievedChunk chunk, string phrase)
    {
        return chunk.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase) ||
               chunk.FileName.Contains(phrase, StringComparison.OrdinalIgnoreCase) ||
               chunk.Title.Contains(phrase, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record QdrantCreateCollectionRequest(
    [property: JsonPropertyName("vectors")] QdrantVectorConfig Vectors);

internal sealed record QdrantVectorConfig(
    [property: JsonPropertyName("size")] int Size,
    [property: JsonPropertyName("distance")] string Distance);

internal sealed record QdrantPoint(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("payload")] Dictionary<string, object?> Payload);

internal sealed record QdrantUpsertRequest(
    [property: JsonPropertyName("points")] IReadOnlyList<QdrantPoint> Points);

internal sealed record QdrantDeleteRequest(
    [property: JsonPropertyName("filter")] QdrantFilter Filter);

internal sealed record QdrantSearchRequest(
    [property: JsonPropertyName("vector")] float[] Vector,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("filter")] QdrantFilter? Filter);

internal sealed record QdrantScrollRequest(
    [property: JsonPropertyName("filter")] QdrantFilter? Filter,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("with_payload")] bool WithPayload,
    [property: JsonPropertyName("with_vector")] bool WithVector,
    [property: JsonPropertyName("offset")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonElement? Offset);

internal sealed record QdrantFilter(
    [property: JsonPropertyName("must")] IReadOnlyList<QdrantMust> Must);

internal sealed record QdrantMust(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("match")] QdrantMatchAny Match);

internal sealed record QdrantMatchAny(
    [property: JsonPropertyName("any")] IReadOnlyList<string> Any);

internal sealed record QdrantSearchResponse(
    [property: JsonPropertyName("result")] IReadOnlyList<QdrantSearchResult>? Result);

internal sealed record QdrantSearchResult(
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("payload")] Dictionary<string, JsonElement>? Payload);

internal sealed record QdrantScrollResponse(
    [property: JsonPropertyName("result")] QdrantScrollResult? Result);

internal sealed record QdrantScrollResult(
    [property: JsonPropertyName("points")] IReadOnlyList<QdrantScrollPoint>? Points,
    [property: JsonPropertyName("next_page_offset")] JsonElement? NextPageOffset);

internal sealed record QdrantScrollPoint(
    [property: JsonPropertyName("payload")] Dictionary<string, JsonElement>? Payload);
