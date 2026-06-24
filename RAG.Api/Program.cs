using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RAG.Core.Configuration;
using RAG.Core.Data;
using RAG.Core.Models;
using RAG.Core.Services;

var builder = WebApplication.CreateBuilder(args);
var maxUploadBytes = builder.Configuration.GetValue<long>(
    "Rag:Ingestion:MaxUploadBytes",
    100 * 1024 * 1024);

builder.Services.AddRagCore(builder.Configuration);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

var app = builder.Build();

app.Logger.LogInformation("Ensuring RAG database is ready.");
await app.Services.EnsureRagDatabaseAsync();
app.Logger.LogInformation("RAG database is ready.");

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/documents", async (RagDbContext dbContext, CancellationToken cancellationToken) =>
{
    var documents = await dbContext.Documents.ToListAsync(cancellationToken);

    return Results.Ok(documents
        .OrderByDescending(document => document.CreatedAtUtc)
        .Select(ToStatusResponse));
});

app.MapGet("/api/documents/{id:guid}", async (Guid id, RagDbContext dbContext, CancellationToken cancellationToken) =>
{
    var document = await dbContext.Documents.SingleOrDefaultAsync(document => document.Id == id, cancellationToken);
    return document is null ? Results.NotFound() : Results.Ok(ToStatusResponse(document));
});

app.MapPost("/api/documents/{id:guid}/reindex", async (Guid id, RagDbContext dbContext, CancellationToken cancellationToken) =>
{
    var document = await dbContext.Documents.SingleOrDefaultAsync(document => document.Id == id, cancellationToken);
    if (document is null)
    {
        return Results.NotFound();
    }

    document.Status = DocumentStatus.Pending;
    document.ErrorMessage = null;
    document.ChunkCount = 0;
    document.ProgressStage = "Queued";
    document.ProgressPercent = 0;
    document.ProcessedChunks = 0;
    document.TotalChunks = 0;
    document.UpdatedAtUtc = DateTimeOffset.UtcNow;
    await dbContext.SaveChangesAsync(cancellationToken);

    return Results.Accepted($"/api/documents/{id}", ToStatusResponse(document));
});

app.MapPost("/api/documents", async (
    HttpRequest request,
    RagDbContext dbContext,
    IObjectStorage storage,
    IOptions<RagOptions> options,
    CancellationToken cancellationToken) =>
{
    try
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { error = "Upload must be multipart form data." });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { error = "A PDF or TXT file is required." });
        }

        if (file.Length > options.Value.Ingestion.MaxUploadBytes)
        {
            return Results.BadRequest(new { error = $"File exceeds the {options.Value.Ingestion.MaxUploadBytes / 1024 / 1024} MB upload limit." });
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not ".pdf" and not ".txt")
        {
            return Results.BadRequest(new { error = "Only .pdf and .txt files are supported." });
        }

        var documentId = Guid.NewGuid();
        var fileName = Path.GetFileName(file.FileName);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType)
            ? extension == ".pdf" ? "application/pdf" : "text/plain"
            : file.ContentType;
        var objectKey = $"{documentId:N}/{fileName}";

        await using var stream = file.OpenReadStream();
        await storage.UploadAsync(objectKey, stream, contentType, cancellationToken);

        var document = new DocumentRecord
        {
            Id = documentId,
            FileName = fileName,
            ContentType = contentType,
            ObjectKey = objectKey,
            Status = DocumentStatus.Pending,
            ProgressStage = "Queued",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Accepted($"/api/documents/{documentId}", new DocumentUploadResponse(documentId, document.Status.ToString()));
    }
    catch (BadHttpRequestException ex)
    {
        app.Logger.LogWarning(ex, "Upload request was rejected.");
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        app.Logger.LogWarning("Upload request was canceled by the client.");
        return Results.Json(new { error = "Upload was canceled before the file was queued." }, statusCode: StatusCodes.Status499ClientClosedRequest);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Upload failed before the document was queued.");
        return Results.Json(new { error = $"Upload failed before the document was queued: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/ask", async (
    AskRequest request,
    IChatAnswerService chatAnswerService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var response = await chatAnswerService.AskAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (AiProviderException ex)
    {
        app.Logger.LogWarning(ex, "AI provider failed while answering a question.");
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (HttpRequestException ex)
    {
        app.Logger.LogWarning(ex, "HTTP dependency failed while answering a question.");
        return Results.Json(new { error = "A downstream AI or vector service request failed. Check the Aspire logs for details." }, statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapFallbackToFile("index.html");

app.Run();

static DocumentStatusResponse ToStatusResponse(DocumentRecord document)
{
    return new DocumentStatusResponse(
        document.Id,
        document.FileName,
        document.ContentType,
        document.Status.ToString(),
        document.ChunkCount,
        document.ProgressStage,
        document.ProgressPercent,
        document.ProcessedChunks,
        document.TotalChunks,
        document.ErrorMessage,
        document.CreatedAtUtc,
        document.UpdatedAtUtc);
}
