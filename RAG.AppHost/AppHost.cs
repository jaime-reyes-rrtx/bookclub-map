var builder = DistributedApplication.CreateBuilder(args);

var dataDir = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "data"));
var databasePath = Path.Combine(dataDir, "rag.db");
var geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
var configuredProvider = Environment.GetEnvironmentVariable("RAG_AI_PROVIDER")
    ?? Environment.GetEnvironmentVariable("Rag__Ai__Provider");
var aiProvider = configuredProvider
    ?? (string.IsNullOrWhiteSpace(geminiApiKey) ? "Ollama" : "Gemini");
var useGemini = aiProvider.Equals("Gemini", StringComparison.OrdinalIgnoreCase);

var qdrant = builder.AddContainer("qdrant", "qdrant/qdrant")
    .WithHttpEndpoint(port: 6333, targetPort: 6333, name: "http")
    .WithEndpoint(port: 6334, targetPort: 6334, name: "grpc")
    .WithVolume("rag-qdrant-data", "/qdrant/storage")
    .WithHttpHealthCheck("/healthz", endpointName: "http");

var minio = builder.AddContainer("minio", "minio/minio")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithHttpEndpoint(port: 9000, targetPort: 9000, name: "api")
    .WithHttpEndpoint(port: 9001, targetPort: 9001, name: "console")
    .WithVolume("rag-minio-data", "/data")
    .WithHttpHealthCheck("/minio/health/ready", endpointName: "api");

var api = builder.AddProject<Projects.RAG_Api>("api", launchProfileName: "http")
    .WithEnvironment("Rag__DatabasePath", databasePath)
    .WithEnvironment("Rag__Storage__ServiceUrl", minio.GetEndpoint("api"))
    .WithEnvironment("Rag__Storage__Bucket", "rag-documents")
    .WithEnvironment("Rag__Storage__AccessKey", "minioadmin")
    .WithEnvironment("Rag__Storage__SecretKey", "minioadmin")
    .WithEnvironment("Rag__Qdrant__BaseUrl", qdrant.GetEndpoint("http"))
    .WithEnvironment("Rag__Qdrant__CollectionName", "rag_chunks")
    .WithEnvironment("Rag__Qdrant__VectorSize", "768")
    .WaitFor(qdrant)
    .WaitFor(minio);

var worker = builder.AddProject<Projects.RAG_Worker>("worker", launchProfileName: "RAG.Worker")
    .WithEnvironment("Rag__DatabasePath", databasePath)
    .WithEnvironment("Rag__Storage__ServiceUrl", minio.GetEndpoint("api"))
    .WithEnvironment("Rag__Storage__Bucket", "rag-documents")
    .WithEnvironment("Rag__Storage__AccessKey", "minioadmin")
    .WithEnvironment("Rag__Storage__SecretKey", "minioadmin")
    .WithEnvironment("Rag__Qdrant__BaseUrl", qdrant.GetEndpoint("http"))
    .WithEnvironment("Rag__Qdrant__CollectionName", "rag_chunks")
    .WithEnvironment("Rag__Qdrant__VectorSize", "768")
    .WaitFor(qdrant)
    .WaitFor(minio);

if (useGemini)
{
    api = api
        .WithEnvironment("Rag__Ai__Provider", "Gemini")
        .WithEnvironment("Rag__Ai__BaseUrl", "https://generativelanguage.googleapis.com/v1beta/")
        .WithEnvironment("Rag__Ai__EmbeddingModel", "gemini-embedding-2")
        .WithEnvironment("Rag__Ai__ChatModel", Environment.GetEnvironmentVariable("GEMINI_CHAT_MODEL") ?? "gemini-2.5-flash-lite")
        .WithEnvironment("GEMINI_API_KEY", geminiApiKey ?? "");

    worker = worker
        .WithEnvironment("Rag__Ai__Provider", "Gemini")
        .WithEnvironment("Rag__Ai__BaseUrl", "https://generativelanguage.googleapis.com/v1beta/")
        .WithEnvironment("Rag__Ai__EmbeddingModel", "gemini-embedding-2")
        .WithEnvironment("Rag__Ai__ChatModel", Environment.GetEnvironmentVariable("GEMINI_CHAT_MODEL") ?? "gemini-2.5-flash-lite")
        .WithEnvironment("GEMINI_API_KEY", geminiApiKey ?? "");
}
else
{
    var ollama = builder.AddContainer("ollama", "ollama/ollama")
        .WithHttpEndpoint(port: 11434, targetPort: 11434, name: "http")
        .WithVolume("rag-ollama-data", "/root/.ollama");

    api = api
        .WithEnvironment("Rag__Ai__Provider", "Ollama")
        .WithEnvironment("Rag__Ai__BaseUrl", ollama.GetEndpoint("http"))
        .WithEnvironment("Rag__Ai__EmbeddingModel", "nomic-embed-text")
        .WithEnvironment("Rag__Ai__ChatModel", "llama3.2")
        .WaitFor(ollama);

    worker = worker
        .WithEnvironment("Rag__Ai__Provider", "Ollama")
        .WithEnvironment("Rag__Ai__BaseUrl", ollama.GetEndpoint("http"))
        .WithEnvironment("Rag__Ai__EmbeddingModel", "nomic-embed-text")
        .WithEnvironment("Rag__Ai__ChatModel", "llama3.2");
}

builder.Build().Run();
