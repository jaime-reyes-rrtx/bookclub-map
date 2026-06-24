using System.Net;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Models;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class QdrantVectorStoreTests
{
    [Fact]
    public async Task SearchAsync_PreservesGeneratedArtifactProvenanceFromPayload()
    {
        var documentId = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return JsonResponse($$"""
                {
                  "result": [
                    {
                      "score": 0.91,
                      "payload": {
                        "documentId": "{{documentId}}",
                        "fileName": "novel.txt",
                        "chunkIndex": -2,
                        "pageNumber": null,
                        "sourceObjectKey": "objects/novel.txt",
                        "text": "Likely protagonists: Alice Morgan.",
                        "chunkType": "literary_book_club_profile",
                        "title": "Book club literary profile",
                        "isGeneratedArtifact": true,
                        "artifactKind": "book-club-profile",
                        "artifactProvider": "Ollama",
                        "artifactModel": "llama3.2",
                        "artifactPromptVersion": "book-club-profile-v1",
                        "generatedAtUtc": "2026-06-24T12:00:00+00:00",
                        "sourceChunkIndexes": [0, 1],
                        "sourcePageNumbers": [3, 4]
                      }
                    }
                  ]
                }
                """);
        });
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://qdrant")
        };
        var store = new QdrantVectorStore(
            httpClient,
            Options.Create(new RagOptions
            {
                Qdrant = new QdrantOptions { CollectionName = "test_chunks" }
            }));

        var chunks = await store.SearchAsync([0.1f, 0.2f], 1, null, CancellationToken.None);

        var chunk = Assert.Single(chunks);
        var provenance = Assert.IsType<ChunkProvenance>(chunk.Provenance);
        Assert.True(provenance.IsGenerated);
        Assert.Equal("book-club-profile", provenance.ArtifactKind);
        Assert.Equal("Ollama", provenance.Provider);
        Assert.Equal("llama3.2", provenance.Model);
        Assert.Equal("book-club-profile-v1", provenance.PromptVersion);
        Assert.Equal([0, 1], provenance.SourceChunkIndexes);
        Assert.Equal([3, 4], provenance.SourcePageNumbers);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
