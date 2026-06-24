# Building a .NET Book Club RAG Pipeline

This tutorial explains the sample project in this repository as a blog series. It is written for engineers who already know basic C# and ASP.NET Core, but are still learning how modern RAG systems are assembled.

The goal is not to present a perfect production architecture. The goal is to show how the pieces connect, where the boundaries are, and why those boundaries matter when building a document-ingestion and question-answering system in .NET.

## Series Overview

The project implements this workflow:

```text
1. Upload PDF/TXT
2. Store original file in object storage
3. Track metadata in SQLite
4. Worker extracts text
5. Generate book-club literary artifacts
6. Chunk source text and artifacts
7. Generate embeddings
8. Store vectors and citation payloads in Qdrant
9. Retrieve relevant chunks for a question
10. Send chunks to an LLM
11. Return answer + citations
```

At a high level, the system has five responsibilities:

- **Orchestration:** Aspire starts the API, worker, Qdrant, MinIO, and optionally Ollama.
- **User interaction:** The API hosts the upload and chat UI.
- **Durable state:** SQLite tracks document status; MinIO stores originals; Qdrant stores vectors.
- **Ingestion:** The worker converts files into searchable chunks.
- **Answering:** The ask service retrieves evidence and asks an LLM to answer from that evidence.

The most important design choice is that the API and worker do not know model-specific request formats. They depend on interfaces such as `IEmbeddingProvider`, `IChatCompletionProvider`, and `IVectorStore`. That makes the sample flexible enough to use Gemini today and later add Azure OpenAI, AWS Bedrock, Vertex AI, OpenAI, or another provider.

## Chapter 1: Solution Topology

The solution is split into small projects:

```text
RAG.AppHost   Aspire orchestration
RAG.Api       ASP.NET Core API + static UI
RAG.Worker    background ingestion loop
RAG.Core      shared domain, providers, storage, vector, EF Core
RAG.Tests     focused unit tests
```

This shape is useful because each project has a clear job:

- `RAG.Api` accepts user input and returns results.
- `RAG.Worker` performs slow ingestion work outside the request path.
- `RAG.Core` holds reusable logic and contracts.
- `RAG.AppHost` wires local infrastructure together.

The user uploads a document to the API, but the API does not process the book immediately. It stores the file, creates a metadata record, and returns quickly. The worker later picks up pending documents and runs extraction, enrichment, chunking, embedding, and vector indexing.

That asynchronous workflow matters because embedding a large PDF may take minutes. Blocking the upload request until all vectors are created would produce timeouts and a poor user experience.

## Chapter 2: Aspire as the Local Control Plane

`RAG.AppHost/AppHost.cs` defines the local environment.

It starts:

- Qdrant on ports `6333` and `6334`.
- MinIO on ports `9000` and `9001`.
- API on `http://127.0.0.1:5080/`.
- Worker as a background process.
- Ollama only when Gemini is not selected.

The AppHost also creates persistent Docker volumes:

- `rag-qdrant-data`
- `rag-minio-data`
- `rag-ollama-data`

Those volumes allow indexed vectors, uploaded files, and downloaded Ollama models to survive container restarts.

Production note: these services are exposed on fixed local ports and MinIO uses sample credentials. That is acceptable for this learning project, which is not intended for production, but a real deployment should use private networking, managed secrets, and locked-down service access.

Aspire injects configuration into the API and worker through environment variables:

```csharp
.WithEnvironment("Rag__Qdrant__BaseUrl", qdrant.GetEndpoint("http"))
.WithEnvironment("Rag__Storage__ServiceUrl", minio.GetEndpoint("api"))
.WithEnvironment("Rag__DatabasePath", databasePath)
```

The double underscore syntax maps environment variables into .NET configuration sections. For example, `Rag__Qdrant__BaseUrl` becomes `Rag:Qdrant:BaseUrl`.

### AI Provider Selection

The AppHost chooses Gemini when `GEMINI_API_KEY` is present:

```text
GEMINI_API_KEY present -> Gemini
otherwise              -> Ollama
```

You can override this with:

```bash
export RAG_AI_PROVIDER="Gemini"
```

| Option | Pros | Cons |
| --- | --- | --- |
| Local LLM with Ollama | Keeps prompts and document content on your machine. Works well for offline experimentation after models are downloaded. Avoids per-request API costs. | Requires local CPU/GPU, memory, and disk resources. Model downloads can be large. Responses are usually slower than hosted APIs on modest hardware. |
| API-hosted LLM with Gemini | Requires no local model hosting. Usually provides faster responses and stronger model quality. Easier to scale beyond one development machine. | Sends prompts and retrieved document context to an external service. Requires an API key, network access, and provider billing/quotas. |

The current Gemini defaults are:

- Embedding model: `gemini-embedding-2`
- Chat model: `gemini-2.5-pro`

The local Ollama defaults are:

- Embedding model: `nomic-embed-text`
- Chat model: `llama3.2`

Note: RAG uses two model types because retrieval and answer generation are different jobs. The embedding model turns document chunks and user questions into numeric vectors, called embeddings, that capture semantic meaning. The vector store uses those embeddings to find chunks related to the question. The chat model then receives the question plus the retrieved chunks and writes the final answer.

## Chapter 3: Shared Configuration and Contracts

`RAG.Core/Configuration/RagOptions.cs` defines strongly typed settings:

- `StorageOptions`
- `AiOptions`
- `QdrantOptions`
- `IngestionOptions`

This keeps configuration access consistent. Instead of reading raw strings throughout the app, services receive `IOptions<RagOptions>`.

The key interfaces live in `RAG.Core/Services/Contracts.cs`.

Important contracts:

- `IObjectStorage`: upload and read original files.
- `ITextExtractor`: extract text from PDFs and TXT files.
- `ITextChunker`: split extracted text into chunks.
- `IEmbeddingProvider`: turn text into vectors.
- `IChatCompletionProvider`: produce final answers from evidence.
- `ILiteraryAnalysisProvider`: generate book-club profiles.
- `IVectorStore`: upsert, search, and retrieve Qdrant chunks.
- `IDocumentIngestionService`: process pending documents.
- `IChatAnswerService`: answer user questions.

These interfaces are the main teaching point of the project. The application workflow depends on stable capabilities, not on a specific vendor SDK.

## Chapter 4: Metadata with SQLite and EF Core

`RAG.Core/Data/DocumentRecord.cs` stores document metadata:

- document ID;
- filename and content type;
- object-storage key;
- ingestion status;
- error message;
- final chunk count;
- progress stage and percentage;
- processed and total chunks;
- timestamps.

The statuses are:

```csharp
Pending
Processing
Indexed
Failed
```

`RAG.Core/Data/RagDbContext.cs` maps this entity with EF Core. This is intentionally simple. SQLite is good enough for a local learning project and gives the API and worker a shared durable state.

The database initializer lives in `ServiceCollectionExtensions.EnsureRagDatabaseAsync`. It uses `EnsureCreatedAsync` and also performs lightweight idempotent column checks for the progress fields. This avoids forcing a developer to delete local data after the model changes during the tutorial.

In a production system, you would normally replace this with formal EF Core migrations.

## Chapter 5: Upload API and UI

The upload endpoint is in `RAG.Api/Program.cs`:

```text
POST /api/documents
```

The endpoint:

1. verifies that the request is multipart form data;
2. requires a PDF or TXT file;
3. enforces the configured upload limit;
4. writes the original file to object storage;
5. creates a `DocumentRecord` with `Pending` status;
6. returns `202 Accepted`.

The API deliberately avoids doing extraction and embedding in the request. It queues work by creating a pending database row.

Production note: the upload limit protects the request body, but this sample still trusts the uploaded file enough to store it and later process it. A production system would usually add stronger content validation, malware scanning, tighter per-user quotas, and generic error responses instead of returning internal exception details. This project keeps the behavior simple because it is a learning project, not production software.

The UI in `RAG.Api/wwwroot` is intentionally plain:

- `index.html`: structure for upload and chat.
- `styles.css`: layout and progress styling.
- `app.js`: calls the API, polls document status, renders answers and citations.

The UI polls `GET /api/documents` every few seconds. For large books, ingestion can take a while, so the worker updates:

- `progressStage`
- `progressPercent`
- `processedChunks`
- `totalChunks`

Production note: polling is simple and works well for this local sample, but a production UI would usually subscribe to status updates instead. For example, the API could publish document progress events through SignalR, WebSockets, Server-Sent Events, or a message broker-backed notification service, and the browser could receive updates as they happen instead of repeatedly asking the API for the full document list.

This lets the UI show useful progress instead of leaving a book stuck at a vague `Processing` state.

## Chapter 6: Object Storage with MinIO

Original files are stored in object storage before indexing. The local implementation is `RAG.Core/Services/S3ObjectStorage.cs`.

MinIO is used because it is S3-compatible and easy to run locally in Docker. The same abstraction could later support:

- AWS S3;
- Azure Blob Storage;
- Google Cloud Storage;
- local filesystem storage.

The object key uses the document ID:

```text
{documentId}/{originalFileName}
```

This avoids filename collisions and makes it easy to trace a stored object back to the metadata row.

Keeping original files matters because vector chunks are derived data. If chunking, extraction, embedding, or analysis changes later, the worker can reprocess the original document.

## Chapter 7: Worker Ingestion Pipeline

`RAG.Worker/Worker.cs` is a polling background service. Every configured interval, it asks `IDocumentIngestionService` to process pending documents.

The worker supports stale processing recovery. If a document is marked `Processing` but has not updated recently, it can be picked up again. This is useful during development when the app is stopped mid-ingestion.

`RAG.Core/Services/DocumentIngestionService.cs` is the pipeline:

1. mark document as `Processing`;
2. verify storage and Qdrant are available;
3. open the original file from MinIO;
4. extract text;
5. chunk source text;
6. generate literary artifacts;
7. combine artifact chunks and source chunks;
8. delete old vectors for the document;
9. generate embeddings;
10. upsert embedded chunks to Qdrant;
11. mark the document `Indexed`;
12. save final progress.

Progress is updated between major stages:

```text
Preparing storage
Extracting text
Chunking text
Building book club profile
Resetting existing index
Generating embeddings
Writing vector index
Ready
```

If any exception occurs, the document is marked `Failed` and the error message is surfaced to the UI.

Production note: reindexing currently deletes the existing vectors before the replacement index has fully succeeded, and ingestion errors are shown directly in the UI for developer visibility. In production, a safer approach would build the replacement index first, swap only after success, and keep detailed exception text in logs rather than user-facing responses. This sample favors readability because it is a learning project.

## Chapter 8: Extracting and Chunking Text

Text extraction lives in `RAG.Core/Services/TextExtractor.cs`.

For TXT files, extraction is a direct UTF-8 read. For PDFs, the project uses PdfPig to extract page text. PDF text extraction is imperfect because PDFs are layout documents, not semantic text documents. That is why citations include page numbers when available, but the extracted text may contain odd spacing or artifacts.

Production note: extraction and chunking are intentionally straightforward and materialize full files, extracted text, token lists, chunks, and embeddings in memory. That can become expensive or unstable with large files even when the upload is under the configured byte limit. A production pipeline would stream where possible, enforce extracted-text and token caps, and batch embedding/vector writes. This repository keeps the simple implementation because the goal is learning the flow, not production hardening.

Chunking lives in `RAG.Core/Services/TextChunker.cs`.

The default ingestion settings are:

```json
{
  "ChunkTokenCount": 800,
  "ChunkOverlapTokens": 100
}
```

The tokenizer is approximate, but the design goal is clear: produce chunks that are large enough to contain useful context and small enough to fit many chunks into an LLM prompt.

Overlap helps preserve continuity. If an important sentence falls near a boundary, overlap gives nearby chunks a chance to retain enough surrounding context.

Note: `800` and `100` are starting values, not magic numbers. A `ChunkTokenCount` of `800` gives each embedded chunk enough room for paragraph-level context, which helps literary questions that depend on surrounding evidence. A smaller value such as `400` can improve pinpoint retrieval for short factual passages, but it creates more chunks, more embedding calls, more vector rows, and more chances to split related ideas apart. `ChunkOverlapTokens` is `100`, or 12.5% of the chunk size, so adjacent chunks share enough context without duplicating too much text. In practice, overlap is often tuned as a ratio, commonly around 10-20%, then adjusted for the document type and observed answer quality.

The main limiting factors are the embedding model input limit, the chat model context window, retrieval count, latency, storage, and cost. Larger chunks reduce indexing volume but can make search results less precise. Smaller chunks improve precision but require retrieving more chunks to answer broader questions. More overlap preserves continuity but increases duplicate embeddings and vector storage. The right values should be measured against the questions the system needs to answer.

## Chapter 9: Literary Artifacts

Simply embedding source text is often not enough. Broad questions like "Who are the protagonists?" or "What are the themes?" may not match one exact passage.

This chapter is where the RAG pipeline starts to become a product instead of a generic document search demo. The raw retrieval system can find passages, but literary artifacts shape the system around the expectations of a book-club user. They give the application its domain behavior: this is what makes it a book-club chat instead of a travel-info chat, a legal-document chat, or a generic PDF chatbot.

The important design question is not only "What text did we index?" It is also "What will real users ask, and what supporting knowledge should exist so the system can answer well?" For a book-club assistant, users often ask about characters, themes, setting, motivations, symbolism, and discussion prompts. Those questions may require synthesis across the whole book, not just one nearby paragraph.

To improve this, the worker generates extra derived chunks:

- a book-club profile;
- a name/entity profile.

These are created by `RAG.Core/Services/LiteraryArtifactGenerator.cs` and the configured `ILiteraryAnalysisProvider`.

The artifact generator reads representative source chunks and asks the selected AI provider to create structured, searchable summaries for the domain. These summaries are not a replacement for source chunks. They are additional retrieval targets that make broad, user-centered questions easier to answer.

The generated artifacts are embedded and stored in Qdrant like normal chunks, but their `chunkType` identifies them:

```text
literary_book_club_profile
literary_name_profile
```

This is an important RAG lesson: the vector database can store both source material and generated support material. The support material should be designed around expected user questions.

In a real project, this is where product analysis matters. A useful RAG system starts with the user's workflow, not only the database schema or model choice. If the product were a travel-info chat, the generated artifacts might focus on destinations, opening hours, transit options, weather, accessibility, and itinerary constraints. If it were a legal assistant, the artifacts might focus on parties, obligations, dates, clauses, risks, and definitions. The retrieval layer should reflect the job the user is trying to get done.

For this MVP, the expected domain is book-club discussion, so the generated profile focuses on:

- likely protagonists;
- major characters;
- setting;
- plot overview;
- character arcs;
- themes;
- motifs;
- discussion questions;
- evidence notes.

## Chapter 10: AI Provider Abstractions

The project supports Ollama and Gemini through provider implementations:

- `OllamaEmbeddingProvider`
- `OllamaChatCompletionProvider`
- `OllamaLiteraryAnalysisProvider`
- `GeminiEmbeddingProvider`
- `GeminiChatCompletionProvider`
- `GeminiLiteraryAnalysisProvider`

Provider selection happens in `ServiceCollectionExtensions.AddAiProviders`.

The rest of the code depends only on:

```csharp
IEmbeddingProvider
IChatCompletionProvider
ILiteraryAnalysisProvider
```

That means adding another provider should be a focused change:

1. implement the provider interfaces;
2. add a configuration branch in `AddAiProviders`;
3. set the provider-specific base URL, model names, and API key source.

A future Azure OpenAI, AWS Bedrock, Vertex AI, or OpenAI provider should not require changes to the ingestion service, API endpoints, or worker orchestration.

## Chapter 11: Qdrant Vector Storage

`RAG.Core/Services/QdrantVectorStore.cs` owns Qdrant interaction.

Each point stores:

- vector;
- `documentId`;
- `fileName`;
- `chunkIndex`;
- `pageNumber`;
- `sourceObjectKey`;
- `text`;
- `chunkType`;
- `title`.

The vector search endpoint returns matching chunks and payloads. Payloads become citations and LLM context.

The store also supports two non-vector retrieval helpers:

- `GetDocumentProfileChunksAsync`
- `GetChunksContainingTextAsync`

These exist because vector search is not always enough. If a user asks a comparison question with named characters, exact-name lookup can ensure each named subject contributes evidence.

That is how the project handles questions like:

```text
Can you find any similarities between Calpurnia and Hermione?
```

Pure top-k semantic retrieval may over-focus on one book. The improved retrieval path combines semantic search, profile chunks, and exact-name chunks.

## Chapter 12: Ask Flow and Retrieval Strategy

The ask endpoint is:

```text
POST /api/ask
```

It accepts:

```json
{
  "question": "Can you compare Calpurnia and Hermione?",
  "documentIds": null
}
```

`RAG.Core/Services/ChatAnswerService.cs` handles the workflow:

1. validate the question;
2. build multiple retrieval queries;
3. embed each query;
4. search Qdrant;
5. detect comparison-style questions;
6. extract named subjects;
7. add matching literary profiles;
8. add exact-name chunks;
9. rank and filter candidates;
10. send selected chunks to the chat provider;
11. return answer and citations.

Production note: this sample validates that the question is present, but it does not enforce a small question-specific length limit. In a production environment, especially with paid hosted models, you would normally cap request size, rate-limit users, and track token/cost usage. The current behavior is fine for a local learning project that is not intended for production.

Broad literary questions get expanded queries:

```text
literary book club profile protagonists major characters themes...
main characters protagonists central people important names...
who are the key people and what roles do they have...
```

Comparison questions get extra handling:

- terms like `similarities`, `compare`, `between`, `both`, `contrast`, and `differences` activate comparison mode;
- capitalized names are treated as named subjects;
- unrelated low-rank documents are filtered out;
- citations are returned for the full context sent to the LLM.

This design is still generic. It is not hardcoded to Calpurnia, Hermione, Harry Potter, or Eisenhorn. It uses question shape and names to retrieve better evidence.

## Chapter 13: Prompting and Citations

The Gemini chat prompt lives in `GeminiChatCompletionProvider`.

The system prompt tells the model to:

- answer directly;
- use only provided excerpts;
- prefer literary artifacts for broad literary questions;
- avoid dumping long passages;
- distinguish evidence from interpretation;
- cite excerpts inline with bracket numbers;
- say what is missing when evidence is insufficient.

The user prompt lists numbered excerpts:

```text
[1] File name, page, chunk
chunk text...

[2] File name, page, chunk
chunk text...
```

The API returns `CitationDto` records with:

- document ID;
- file name;
- chunk index;
- page number;
- score;
- snippet.

The UI displays these under the answer so the user can inspect where the response came from.

## Chapter 14: Testing the Pipeline

The tests are intentionally focused rather than exhaustive.

`TextChunkerTests` checks chunk sizing and overlap behavior.

`AiProviderRegistrationTests` verifies provider selection through configuration.

`ChatAnswerServiceTests` verifies:

- retrieved chunks become citations;
- broad character questions expand retrieval;
- protagonist questions can use document profiles;
- comparison questions retrieve evidence for each named subject;
- unrelated documents are filtered from comparison context.

Run tests with:

```bash
dotnet test RAGPipeline.sln
```

The project also benefits from manual testing because RAG behavior depends on real documents, model output, and vector search quality.

Recommended manual checks:

1. Upload a short TXT file.
2. Upload a PDF book.
3. Confirm progress moves through ingestion stages.
4. Ask a direct question about one document.
5. Ask a broad character question.
6. Ask a cross-document comparison question.
7. Confirm citations reference the expected documents.

## Chapter 15: Local Development Notes

Useful commands:

```bash
dotnet build RAGPipeline.sln
dotnet test RAGPipeline.sln
dotnet run --project RAG.AppHost/RAG.AppHost.csproj
```

With Gemini:

```bash
export GEMINI_API_KEY="your-key"
export RAG_AI_PROVIDER="Gemini"
dotnet run --project RAG.AppHost/RAG.AppHost.csproj
```

With local Ollama:

```bash
unset GEMINI_API_KEY
unset RAG_AI_PROVIDER
dotnet run --project RAG.AppHost/RAG.AppHost.csproj
```

Local URLs:

- UI/API: `http://127.0.0.1:5080/`
- Dashboard: `https://localhost:17071`
- Qdrant: `http://localhost:6333`
- MinIO API: `http://localhost:9000`
- MinIO console: `http://localhost:9001`

The Aspire dashboard may show local HTTPS development certificate warnings. Those are separate from the API, which is served over HTTP in this sample.

## Chapter 16: What This MVP Teaches

This project demonstrates several RAG practices that matter in real systems:

- Store original documents separately from vectors.
- Track ingestion as durable state.
- Keep long-running ingestion outside request/response paths.
- Use provider-neutral abstractions for AI services.
- Embed generated metadata, not only raw source text.
- Tune retrieval based on expected question types.
- Combine vector search with structured and lexical retrieval.
- Return citations so users can inspect evidence.
- Surface ingestion failures and progress to the UI.

It also shows where the next production steps would be:

- replace ad hoc SQLite schema updates with migrations;
- add authentication and authorization;
- add document deletion and reindex controls;
- support cloud object storage directly;
- add provider implementations for Azure OpenAI, Bedrock, Vertex AI, or OpenAI;
- add observability around token usage, latency, and provider errors;
- improve PDF extraction quality;
- add background queue infrastructure for multi-worker deployments.

The MVP is complete enough to teach how the pieces connect, while still being small enough for engineers to read end to end.
