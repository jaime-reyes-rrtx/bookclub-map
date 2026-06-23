# Repository Guidelines

## Project Structure & Module Organization

This repository contains a .NET Aspire RAG sample targeting `net10.0`.

- `RAG.AppHost/` orchestrates the API, worker, Qdrant, MinIO, and Ollama resources.
- `RAG.Api/` hosts upload/status/ask endpoints and the static web UI in `wwwroot/`.
- `RAG.Worker/` polls pending documents and performs extraction, chunking, embedding, and indexing.
- `RAG.Core/` contains shared models, EF Core metadata storage, provider abstractions, and service implementations.
- `RAG.Tests/` contains fast unit tests for core behavior.
- `data/`, `bin/`, and `obj/` are generated local/runtime outputs and should not be edited by hand.

## Build, Test, and Development Commands

Run commands from the repository root unless noted otherwise.

- `dotnet restore RAGPipeline.sln` restores NuGet packages.
- `dotnet build RAGPipeline.sln` compiles all projects.
- `dotnet test RAGPipeline.sln` runs the xUnit test suite.
- `dotnet run --project RAG.AppHost/RAG.AppHost.csproj` starts the local Aspire stack.
- Open `http://localhost:5080` for the API UI after AppHost startup.

## Coding Style & Naming Conventions

Use standard C# conventions: four-space indentation, PascalCase for types and public members, camelCase for local variables and parameters, and file names that match primary type names where applicable. Keep nullable annotations clean because `<Nullable>enable</Nullable>` is set. Keep provider-specific code behind interfaces such as `IEmbeddingProvider`, `IChatCompletionProvider`, `IObjectStorage`, and `IVectorStore`.

Run `dotnet format RAGPipeline.sln` before larger changes if formatting drift appears.

## Testing Guidelines

Tests use xUnit in `RAG.Tests/`. Name test files after the unit under test, for example `TextChunkerTests.cs`, and use descriptive method names such as `Chunk_CreatesOverlappingChunksWithStableMetadata`.

Keep unit tests focused on observable behavior. Use Aspire/Docker smoke tests for Qdrant, MinIO, and Ollama integration rather than mocking container internals.

## Commit & Pull Request Guidelines

This checkout has no local Git history, so no repository-specific commit convention can be inferred. Use short imperative commit subjects, for example `Add Aspire app host configuration`, and keep unrelated changes in separate commits.

Pull requests should include a brief summary, validation steps such as `dotnet build RAGPipeline.sln` and `dotnet test RAGPipeline.sln`, and any configuration changes. Link related issues when available. Include screenshots for UI-facing changes.

## Security & Configuration Tips

Do not commit real secrets in `appsettings*.json`. The checked-in MinIO credentials are local sample defaults only. Use .NET user secrets or environment variables for cloud provider keys. Keep local databases, uploaded files, and container volumes out of source control.
