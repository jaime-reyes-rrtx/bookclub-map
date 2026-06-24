using RAG.Core.Models;

namespace RAG.Core.Services;

public sealed class AiProviderException(string message, Exception? innerException = null) : Exception(message, innerException);

public interface IObjectStorage
{
    Task EnsureReadyAsync(CancellationToken cancellationToken);
    Task UploadAsync(string objectKey, Stream content, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
}

public interface ITextExtractor
{
    Task<ExtractedDocument> ExtractAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken);
}

public interface ITextChunker
{
    IReadOnlyList<TextChunk> Chunk(Guid documentId, string fileName, string objectKey, ExtractedDocument document);
}

public interface IEmbeddingProvider
{
    Task<float[]> GenerateEmbeddingAsync(string input, CancellationToken cancellationToken);
}

public interface IChatCompletionProvider
{
    Task<string> GenerateAnswerAsync(string question, IReadOnlyList<RetrievedChunk> chunks, CancellationToken cancellationToken);
}

public interface ILiteraryAnalysisProvider
{
    Task<string> GenerateBookClubProfileAsync(
        string fileName,
        IReadOnlyList<string> candidateNames,
        IReadOnlyList<string> excerpts,
        CancellationToken cancellationToken);
}

public interface ILiteraryArtifactGenerator
{
    Task<IReadOnlyList<TextChunk>> GenerateArtifactsAsync(
        Guid documentId,
        string fileName,
        string objectKey,
        ExtractedDocument document,
        IReadOnlyList<TextChunk> sourceChunks,
        CancellationToken cancellationToken);
}

public interface IAiProviderFactory
{
    IEmbeddingProvider Embeddings { get; }
    IChatCompletionProvider Chat { get; }
}

public interface IVectorStore
{
    Task EnsureCollectionAsync(CancellationToken cancellationToken);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken);
    Task UpsertChunksAsync(IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        float[] embedding,
        int limit,
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RetrievedChunk>> GetDocumentProfileChunksAsync(
        IReadOnlyCollection<Guid>? documentIds,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RetrievedChunk>> GetChunksContainingTextAsync(
        IReadOnlyCollection<string> terms,
        IReadOnlyCollection<Guid>? documentIds,
        int limitPerTerm,
        CancellationToken cancellationToken);
}

public interface IDocumentIngestionService
{
    Task<int> IngestPendingDocumentsAsync(CancellationToken cancellationToken);
    Task IngestDocumentAsync(Guid documentId, CancellationToken cancellationToken);
}

public interface IChatAnswerService
{
    Task<AskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken);
}
