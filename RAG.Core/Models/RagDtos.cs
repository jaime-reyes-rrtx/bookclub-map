namespace RAG.Core.Models;

public sealed record DocumentUploadResponse(Guid DocumentId, string Status);

public sealed record DocumentStatusResponse(
    Guid DocumentId,
    string FileName,
    string ContentType,
    string Status,
    int ChunkCount,
    string ProgressStage,
    int ProgressPercent,
    int ProcessedChunks,
    int TotalChunks,
    string? ErrorMessage,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AskRequest(string Question, Guid[]? DocumentIds, bool IncludeDiagnostics = false);

public sealed record AskResponse(
    string Answer,
    IReadOnlyList<CitationDto> Citations,
    RetrievalDiagnostics? Diagnostics = null);

public sealed record CitationDto(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    int? PageNumber,
    double Score,
    string Snippet,
    string ChunkType = "source",
    string? Title = null,
    bool IsGeneratedArtifact = false,
    string? ArtifactKind = null);

public sealed record RetrievalDiagnostics(
    string Question,
    IReadOnlyList<RetrievalQueryDiagnostic> Queries,
    IReadOnlyList<RetrievedCandidateDiagnostic> Candidates,
    IReadOnlyList<SelectedContextDiagnostic> SelectedContext,
    bool IsComparisonQuestion,
    IReadOnlyList<string> NamedSubjects);

public sealed record RetrievalQueryDiagnostic(string Text, string? NamedSubject);

public sealed record RetrievedCandidateDiagnostic(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    string ChunkType,
    string? Title,
    double VectorScore,
    double FinalRank,
    IReadOnlyList<string> RankReasons,
    bool Selected);

public sealed record SelectedContextDiagnostic(
    Guid DocumentId,
    string FileName,
    int ChunkIndex,
    string ChunkType,
    string? Title,
    double FinalRank,
    string Snippet);

public sealed record ChunkProvenance(
    bool IsGenerated = false,
    string? ArtifactKind = null,
    string? Provider = null,
    string? Model = null,
    string? PromptVersion = null,
    DateTimeOffset? GeneratedAtUtc = null,
    IReadOnlyList<int>? SourceChunkIndexes = null,
    IReadOnlyList<int>? SourcePageNumbers = null);

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
    string Title = "",
    ChunkProvenance? Provenance = null);

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
    string Title = "",
    ChunkProvenance? Provenance = null);
