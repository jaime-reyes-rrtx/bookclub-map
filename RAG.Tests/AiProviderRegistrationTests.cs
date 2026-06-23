using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RAG.Core.Services;

namespace RAG.Tests;

public sealed class AiProviderRegistrationTests
{
    [Fact]
    public void AddRagCore_UsesGeminiProviders_WhenConfigured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Rag:DatabasePath"] = "data/test-registration.db",
                ["Rag:Ai:Provider"] = "Gemini",
                ["Rag:Ai:BaseUrl"] = "https://generativelanguage.googleapis.com/v1beta/",
                ["Rag:Ai:EmbeddingModel"] = "gemini-embedding-2",
                ["Rag:Ai:ChatModel"] = "gemini-2.5-pro",
                ["Rag:Qdrant:BaseUrl"] = "http://localhost:6333",
                ["Rag:Qdrant:VectorSize"] = "768"
            })
            .Build();

        using var services = new ServiceCollection()
            .AddRagCore(configuration)
            .BuildServiceProvider();

        Assert.IsType<GeminiEmbeddingProvider>(services.GetRequiredService<IEmbeddingProvider>());
        Assert.IsType<GeminiChatCompletionProvider>(services.GetRequiredService<IChatCompletionProvider>());
        Assert.IsType<GeminiLiteraryAnalysisProvider>(services.GetRequiredService<ILiteraryAnalysisProvider>());
    }
}
