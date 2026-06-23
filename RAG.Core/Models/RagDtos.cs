namespace RAG.Core.Models;

public sealed record DocumentUploadResponse(Guid DocumentId, string Status);

public sealed record DocumentStatusResponse(
    Guid DocumentId,
    string FileName,
    string ContentType,
    string Status,
    int ChunkCount,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AskRequest(string Question, Guid[]? DocumentIds);

public sealed record AskResponse(string Answer, IReadOnlyList<CitationDto> Citations);

public sealed record CitationDto(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int? PageNumber,
    double Score,
    string Snippet);

public sealed record ExtractedPage(int? PageNumber, string Text);

public sealed record ExtractedDocument(IReadOnlyList<ExtractedPage> Pages);

public sealed record TextChunk(
    Guid Id,
    Guid DocumentId,
    string FileName,
    string ObjectKey,
    int ChunkIndex,
    int? PageNumber,
    string Text,
    string ChunkType = "source",
    string Title = "");

public sealed record EmbeddedChunk(TextChunk Chunk, float[] Embedding);

public sealed record RetrievedChunk(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int? PageNumber,
    string ObjectKey,
    string Text,
    double Score,
    string ChunkType = "source",
    string Title = "");
