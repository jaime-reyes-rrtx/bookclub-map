# Book Club RAG Pipeline

This repository is a learning project that demonstrates how to build a Retrieval-Augmented Generation (RAG) pipeline with .NET, ASP.NET Core, .NET Aspire, Docker containers, Qdrant, object storage, and pluggable AI providers.

The MVP lets a user upload PDF or TXT books, stores the original files, extracts and enriches text, indexes chunks into a vector database, and provides a chat interface that answers questions with citations. The sample is intentionally built as a book-club style assistant so it can answer literary questions about protagonists, themes, character similarities, summaries, and cross-book comparisons.

## Architecture

```text
Browser UI
  -> ASP.NET Core API
  -> SQLite metadata store
  -> MinIO object storage
  -> Background Worker
  -> Text extractor
  -> Literary artifact generator
  -> Chunker
  -> Embedding provider
  -> Qdrant vector database
  -> Chat completion provider
  -> Answer + citations
```

.NET Aspire orchestrates the local system:

- `RAG.AppHost`: starts API, worker, Qdrant, MinIO, and optionally Ollama.
- `RAG.Api`: serves the upload/chat UI and HTTP endpoints.
- `RAG.Worker`: processes pending documents in the background.
- `RAG.Core`: shared models, EF Core metadata, providers, vector store, ingestion, and ask logic.
- `RAG.Tests`: focused unit tests and deterministic golden-question evals for chunking, provider registration, retrieval behavior, citations, and guardrails.

## Technologies

- .NET 10 and ASP.NET Core minimal APIs.
- .NET Aspire AppHost for local orchestration.
- Docker Desktop for containerized dependencies.
- Qdrant for vector search.
- MinIO as local S3-compatible object storage.
- SQLite with EF Core for document metadata and ingestion status.
- Gemini or Ollama for embeddings and chat completion.
- PdfPig for PDF text extraction.
- Plain HTML/CSS/JavaScript for the sample UI.

## Prerequisites

- .NET SDK 10.
- Docker Desktop running.
- Git.
- Optional: `GEMINI_API_KEY` for Gemini-backed AI calls.

Docker memory is a ceiling, not an immediate allocation. If Docker Desktop is configured for 24 GB, it can use up to that amount when containers need it, but idle containers and an idle Docker daemon should leave most memory available to the host.

## Running Locally

Use Gemini if you have a key:

```bash
export GEMINI_API_KEY="your-key"
export RAG_AI_PROVIDER="Gemini"
dotnet run --project RAG.AppHost/RAG.AppHost.csproj
```

Or run without Gemini to use the local Ollama container path:

```bash
unset GEMINI_API_KEY
unset RAG_AI_PROVIDER
dotnet run --project RAG.AppHost/RAG.AppHost.csproj
```

Aspire exposes:

- App UI/API: `http://127.0.0.1:5080/`
- Aspire dashboard: `https://localhost:17071`
- Qdrant: `http://localhost:6333`
- MinIO API: `http://localhost:9000`
- MinIO console: `http://localhost:9001`

The Aspire dashboard is configured for anonymous local access through launch settings.

## Common Commands

```bash
dotnet build RAGPipeline.sln
dotnet test RAGPipeline.sln
dotnet run --project RAG.AppHost/RAG.AppHost.csproj
```

Health check:

```bash
curl http://127.0.0.1:5080/health
```

Ask endpoint example:

```bash
curl http://127.0.0.1:5080/api/ask \
  -H "content-type: application/json" \
  --data '{"question":"Can you compare two characters from the uploaded books?"}'
```

## Configuration

Configuration lives under the `Rag` section in `appsettings.json` and is overridden by Aspire environment variables.

Important settings:

- `Rag:DatabasePath`: SQLite metadata database path.
- `Rag:Storage:*`: MinIO/S3-compatible object storage settings.
- `Rag:Qdrant:*`: vector database URL, collection name, and vector size.
- `Rag:Ai:*`: provider, base URL, embedding model, chat model, timeout.
- `Rag:Ingestion:*`: chunk size, overlap, upload limit, and worker polling interval.
- `Rag:Request:*`: question length, selected-document count, retrieval-query cap, and provider timeout guardrails.

The AppHost automatically chooses Gemini when `GEMINI_API_KEY` is present, unless `RAG_AI_PROVIDER` or `Rag__Ai__Provider` is set explicitly.

## Learning Goals

This project is not intended to be a production-ready RAG platform. It is a practical teaching sample for:

- separating API, worker, domain logic, storage, and AI provider concerns;
- using Aspire to run a multi-service local development environment;
- designing provider-neutral AI abstractions;
- storing original documents separately from vector chunks;
- enriching documents with generated literary artifacts before embedding;
- tracking provenance for generated retrieval artifacts;
- balancing semantic, profile, and exact-name retrieval;
- inspecting retrieval behavior through optional diagnostics;
- replacing heuristic reranking or database polling through small interfaces;
- deleting and reindexing documents through lifecycle endpoints;
- returning answers with traceable citations and optional retrieval diagnostics.

See [Sandy Brook Labs RAG guide](https://sandybrook.io/guides/rag) for a deeper walkthrough.
