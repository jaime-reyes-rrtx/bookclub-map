namespace RAG.Core.Configuration;

public sealed class RagOptions
{
    public string DatabasePath { get; set; } = Path.Combine("data", "rag.db");
    public StorageOptions Storage { get; set; } = new();
    public AiOptions Ai { get; set; } = new();
    public QdrantOptions Qdrant { get; set; } = new();
    public IngestionOptions Ingestion { get; set; } = new();
}

public sealed class StorageOptions
{
    public string Provider { get; set; } = "S3";
    public string Bucket { get; set; } = "rag-documents";
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    public string AccessKey { get; set; } = "minioadmin";
    public string SecretKey { get; set; } = "minioadmin";
    public string Region { get; set; } = "us-east-1";
}

public sealed class AiOptions
{
    public string Provider { get; set; } = "Ollama";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ApiKey { get; set; } = "";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string ChatModel { get; set; } = "llama3.2";
    public int TimeoutSeconds { get; set; } = 180;
}

public sealed class QdrantOptions
{
    public string BaseUrl { get; set; } = "http://localhost:6333";
    public string CollectionName { get; set; } = "rag_chunks";
    public int VectorSize { get; set; } = 768;
}

public sealed class IngestionOptions
{
    public int ChunkTokenCount { get; set; } = 800;
    public int ChunkOverlapTokens { get; set; } = 100;
    public long MaxUploadBytes { get; set; } = 25 * 1024 * 1024;
    public int PollIntervalSeconds { get; set; } = 5;
}
