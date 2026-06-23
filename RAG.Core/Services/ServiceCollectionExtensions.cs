using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Data;

namespace RAG.Core.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRagCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RagOptions>(configuration.GetSection("Rag"));

        services.AddDbContext<RagDbContext>((serviceProvider, options) =>
        {
            var ragOptions = serviceProvider.GetRequiredService<IOptions<RagOptions>>().Value;
            var databasePath = Path.GetFullPath(ragOptions.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            options.UseSqlite($"Data Source={databasePath}");
        });

        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        services.AddSingleton<ITextExtractor, TextExtractor>();
        services.AddSingleton<ITextChunker, TokenTextChunker>();
        services.AddScoped<ILiteraryArtifactGenerator, LiteraryArtifactGenerator>();
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
        services.AddScoped<IChatAnswerService, ChatAnswerService>();
        services.AddScoped<IAiProviderFactory, AiProviderFactory>();

        AddAiProviders(services, configuration);

        services.AddHttpClient<IVectorStore, QdrantVectorStore>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<RagOptions>>().Value.Qdrant;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        return services;
    }

    private static void AddAiProviders(IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Rag:Ai:Provider"] ?? "Ollama";
        if (provider.Equals("Gemini", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IEmbeddingProvider, GeminiEmbeddingProvider>(ConfigureAiClient);
            services.AddHttpClient<GeminiChatCompletionProvider>(ConfigureAiClient);
            services.AddScoped<IChatCompletionProvider>(serviceProvider =>
                serviceProvider.GetRequiredService<GeminiChatCompletionProvider>());
            services.AddScoped<ILiteraryAnalysisProvider, GeminiLiteraryAnalysisProvider>();
            return;
        }

        if (!provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported AI provider '{provider}'. Supported providers: Ollama, Gemini.");
        }

        services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>(ConfigureAiClient);
        services.AddHttpClient<IChatCompletionProvider, OllamaChatCompletionProvider>(ConfigureAiClient);
        services.AddHttpClient<ILiteraryAnalysisProvider, OllamaLiteraryAnalysisProvider>(ConfigureAiClient);
    }

    private static void ConfigureAiClient(IServiceProvider serviceProvider, HttpClient client)
    {
        var options = serviceProvider.GetRequiredService<IOptions<RagOptions>>().Value.Ai;
        client.BaseAddress = new Uri(options.BaseUrl.EndsWith('/') ? options.BaseUrl : options.BaseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    public static async Task EnsureRagDatabaseAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);
    }
}
