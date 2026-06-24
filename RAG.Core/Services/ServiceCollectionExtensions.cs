using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Data;
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
        services.AddSingleton<ITokenEstimator, ApproximateTokenEstimator>();
        services.AddSingleton<ITextChunker, TokenTextChunker>();
        services.AddSingleton<IRetrievalReranker, HeuristicRetrievalReranker>();
        services.AddScoped<ILiteraryArtifactGenerator, LiteraryArtifactGenerator>();
        services.AddScoped<IIngestionWorkSource, DatabaseIngestionWorkSource>();
        services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
        services.AddScoped<IDocumentManagementService, DocumentManagementService>();
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
        await EnsureDocumentProgressColumnsAsync(dbContext, cancellationToken);
    }

    private static async Task EnsureDocumentProgressColumnsAsync(RagDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Documents');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        await AddColumnIfMissingAsync(connection, columns, "ProgressStage", "TEXT NOT NULL DEFAULT 'Queued'", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "ProgressPercent", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "ProcessedChunks", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(connection, columns, "TotalChunks", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await BackfillDocumentProgressAsync(connection, cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(
        System.Data.Common.DbConnection connection,
        HashSet<string> columns,
        string name,
        string definition,
        CancellationToken cancellationToken)
    {
        if (columns.Contains(name))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE Documents ADD COLUMN {name} {definition};";
        await command.ExecuteNonQueryAsync(cancellationToken);
        columns.Add(name);
    }

    private static async Task BackfillDocumentProgressAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Documents
            SET ProgressStage = CASE
                    WHEN Status = 'Indexed' THEN 'Ready'
                    WHEN Status = 'Failed' THEN 'Failed'
                    ELSE ProgressStage
                END,
                ProgressPercent = CASE
                    WHEN Status = 'Indexed' THEN 100
                    ELSE ProgressPercent
                END,
                ProcessedChunks = CASE
                    WHEN Status = 'Indexed' THEN ChunkCount
                    ELSE ProcessedChunks
                END,
                TotalChunks = CASE
                    WHEN Status = 'Indexed' THEN ChunkCount
                    ELSE TotalChunks
                END
            WHERE (Status = 'Indexed' AND ProgressPercent < 100)
               OR (Status = 'Failed' AND ProgressStage <> 'Failed');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
