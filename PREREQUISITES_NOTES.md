# Prerequisites Notes

These notes capture the local system checks performed before implementing the Aspire-contained RAG sample.

## Current Laptop Status

- OS: macOS 26.5.1 on arm64.
- CPU: 12 cores.
- RAM: 36 GB host memory.
- Disk: about 794 GiB available in the repository volume.
- Docker Desktop memory limit: 24 GB, configured as a ceiling. Docker uses memory as containers need it; it does not immediately consume the full limit when idle.

## Installed Tooling

- .NET SDK 10.0.301 is installed.
- Existing solution builds successfully with `dotnet build RAGPipeline.sln --no-restore`.
- Docker CLI 29.5.3 is installed.
- Docker Desktop daemon is running and usable.
- Docker Compose v5.1.4 is installed.
- Git 2.50.1 is installed.
- Standard .NET templates are available:
  - `webapi`
  - `worker`
  - `classlib`
  - `xunit`
  - `blazor`

## Connectivity

- NuGet is reachable.
- Docker registry is reachable.
- Ollama website is reachable.

## Gaps and Warnings

- Docker had no local images during the check, so the first Aspire run will need to pull container images.
- Ollama CLI is not installed locally. This is not blocking because the plan uses Ollama in a container.
- Aspire-specific `dotnet new aspire` templates are not installed. This is not blocking because the current Aspire AppHost builds, and new projects can be created with standard .NET templates.
- Port 5000 is already in use by `ControlCe`.
- Qdrant ports 6333/6334, MinIO ports 9000/9001, Ollama port 11434, Aspire dashboard port 18888, and port 5001 appeared free.
- The repository folder is not currently a Git repository.

## Practical Defaults

- Docker Desktop at 24 GB RAM is appropriate for running local LLM containers.
- Qdrant, MinIO, API, and worker should use modest memory.
- Ollama model loading and inference will be the main driver of Docker memory usage.
- If Docker is running with no containers, most host RAM should remain available to macOS and other applications.
