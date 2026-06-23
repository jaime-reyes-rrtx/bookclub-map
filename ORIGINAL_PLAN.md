# Aspire-Contained RAG Sample Project Plan

## Summary

Build a sample .NET Aspire solution that runs the full PDF/TXT ingestion and question-answering workflow locally through Aspire-managed projects and Docker containers. The app will include an ASP.NET Core API, a simple UI, a background ingestion worker, Qdrant for vector search, object storage for original files, and a local containerized LLM/embedding provider by default.

Default local stack:

- ASP.NET Core API for upload, document status, and ask endpoints.
- Background Worker Service for extraction, chunking, embedding, and indexing.
- Qdrant container for vector storage.
- MinIO container as S3-compatible object storage for original uploads.
- Ollama container for the default local embeddings and chat completion provider.
- Optional Blazor or minimal web UI hosted by the API for upload and chat.

## Key Changes

- Expand the Aspire solution into these projects:
  - `RAG.AppHost`: Aspire orchestration for API, worker, Qdrant, MinIO, and Ollama containers.
  - `RAG.Api`: ASP.NET Core API plus lightweight UI.
  - `RAG.Worker`: background document processing pipeline.
  - `RAG.Core`: shared contracts, pipeline interfaces, DTOs, chunking logic, and provider abstractions.
  - `RAG.Tests`: unit and integration tests.
- Configure Aspire resources:
  - Qdrant container with persistent volume and exposed internal endpoint.
  - MinIO container with persistent volume, access key, secret key, and default bucket.
  - Ollama container with persistent model cache volume as the default local AI provider.
  - API and worker receive all service endpoints through Aspire service discovery and configuration.
- Store original uploaded files in MinIO using an `IObjectStorage` abstraction. Keep the abstraction compatible with Azure Blob or filesystem storage later, but implement MinIO first.
- Use a small metadata store for document/job state. For the sample, use SQLite via EF Core in the API/worker with Aspire-managed connection settings. Track document ID, filename, content type, object-storage key, ingestion status, error message, chunk count, and timestamps.
- Use Qdrant collections with payload metadata for citations:
  - `documentId`
  - `fileName`
  - `chunkIndex`
  - `pageNumber` when available
  - `sourceObjectKey`
  - `text`
- Keep AI integration provider-neutral. Define separate abstractions for embeddings and chat completion, with Ollama as the first adapter and room for AWS Bedrock, Vertex AI, Azure OpenAI, OpenAI, or another HTTP-based provider.
- Use Semantic Kernel only behind the chat/embedding abstractions, not directly from controllers or worker orchestration code.
- Select providers through configuration, for example `AI:Provider=Ollama`, `AI:EmbeddingModel`, and `AI:ChatModel`.

## Workflow Implementation

- Upload flow:
  - `POST /api/documents` accepts `.pdf` and `.txt` multipart uploads.
  - Validate extension, content type, and max file size.
  - Save the original file to MinIO.
  - Create a pending document record.
  - Enqueue ingestion work for the worker.
  - Return `{ documentId, status }`.
- Ingestion worker:
  - Poll pending documents from SQLite or consume an in-process durable queue pattern backed by document status.
  - Download original file from MinIO.
  - Extract text:
    - TXT: direct UTF-8 text read.
    - PDF: use a .NET PDF text extraction library.
  - Chunk extracted text into approximately 800-token chunks with overlap of about 100 tokens.
  - Generate embeddings for each chunk through the configured `IEmbeddingProvider`.
  - Upsert vectors into Qdrant with citation payloads.
  - Mark document as indexed or failed.
- Ask flow:
  - `POST /api/ask` accepts `{ question, documentIds? }`.
  - Generate an embedding for the question.
  - Query Qdrant for the top 5 matching chunks, optionally filtered by `documentIds`.
  - Send the question and retrieved chunks to the configured `IChatCompletionProvider`.
  - Return answer and citations:
    - answer text
    - cited filename
    - page number when available
    - chunk index
    - relevance score
    - snippet
- UI:
  - Provide one upload view with document status.
  - Provide one chat view with an input box, answer display, and citation list.
  - Poll document status until indexing completes.

## Public Interfaces

- API endpoints:
  - `POST /api/documents`
  - `GET /api/documents`
  - `GET /api/documents/{id}`
  - `POST /api/ask`
- Core abstractions:
  - `IObjectStorage`
  - `ITextExtractor`
  - `ITextChunker`
  - `IEmbeddingProvider`
  - `IChatCompletionProvider`
  - `IAiProviderFactory`
  - `IVectorStore`
  - `IChatAnswerService`
  - `IDocumentIngestionService`
- Main DTOs:
  - `DocumentUploadResponse`
  - `DocumentStatusResponse`
  - `AskRequest`
  - `AskResponse`
  - `CitationDto`

## Test Plan

- Unit tests:
  - TXT extraction preserves content.
  - PDF extraction returns text for a known sample PDF.
  - Chunker produces roughly 800-token chunks with configured overlap.
  - Upload validation rejects unsupported file types and oversized files.
  - Ask service includes exactly the retrieved top chunks in the LLM context.
  - AI provider selection resolves the configured embedding and chat adapters.
- Integration tests:
  - API upload stores original file and creates a pending document record.
  - Worker processes a sample TXT document and writes vectors to Qdrant.
  - Ask endpoint returns an answer with up to 5 citations.
- Manual validation:
  - `dotnet run --project RAG.AppHost`
  - Open Aspire dashboard.
  - Upload a TXT and PDF file.
  - Confirm document status changes from pending to indexed.
  - Ask a question and verify answer citations reference uploaded documents.

## Assumptions

- Use MinIO as the default S3-compatible object store because it keeps storage containerized and later maps cleanly to S3-compatible production storage.
- Use Ollama as the default local containerized embedding and chat provider to keep the sample self-contained.
- Keep all model-specific request/response handling inside provider adapters. Core ingestion and ask workflows should depend only on `IEmbeddingProvider` and `IChatCompletionProvider`.
- Provider adapters should support separate embedding and chat models because many platforms expose them as different model families.
- Use SQLite for sample metadata because it is simple, durable, and enough for a local Aspire demo.
- Use Qdrant as the only vector database.
- Keep authentication out of scope for the initial sample.
- Keep ingestion asynchronous; uploads should return quickly and not block on embedding/indexing.
