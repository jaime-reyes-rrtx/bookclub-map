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
                ["title"] = chunk.Chunk.Title
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

    private static RetrievedChunk? ToRetrievedChunk(QdrantSearchResult result)
    {
        if (result.Payload is null)
        {
            return null;
        }

        var payload = result.Payload;
        var documentId = ReadString(payload, "documentId");
        if (!Guid.TryParse(documentId, out var parsedDocumentId))
        {
            return null;
        }

        return new RetrievedChunk(
            parsedDocumentId,
            ReadString(payload, "fileName"),
            ReadInt(payload, "chunkIndex") ?? 0,
            ReadInt(payload, "pageNumber"),
            ReadString(payload, "sourceObjectKey"),
            ReadString(payload, "text"),
            result.Score,
            ReadString(payload, "chunkType") is { Length: > 0 } chunkType ? chunkType : "source",
            ReadString(payload, "title"));
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
