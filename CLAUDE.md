# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

ASP.NET Core Web API (.NET 10, SDK pinned via `global.json`; single project `ChatBotRAGAPI/`, solution `ChatBotRAGAPI.slnx`) that implements a retrieval-augmented-generation chatbot: content is ingested from URLs or uploaded files, chunked, embedded, stored in a vector store, and then queried via `/api/chat/ask`. There is no test project.

## Commands

Run from the repo root (or `ChatBotRAGAPI/`):

```bash
dotnet build ChatBotRAGAPI.slnx
dotnet run --project ChatBotRAGAPI --launch-profile http    # http://localhost:5170, Swagger at /swagger (Development only)
docker compose -f ChatBotRAGAPI/docker-compose.yml up -d    # Qdrant :6333 + Ollama :11434
docker exec ollama ollama pull nomic-embed-text             # embedding model
docker exec ollama ollama pull phi3:mini                    # chat model (appsettings "ChatModel")
```

Manual API testing: `ChatBotRAGAPI/ChatBotRAGAPI.http` and `ChatBotRAGAPI/ChatBotRAGAPI.postman_collection.json`. Sample ingestion inputs (txt/csv/docx/xlsx) live in `ChatBotRAGAPI/testdata/`.

## Architecture

**Pluggable backends chosen at startup** (`Program.cs`, driven by the `Rag` config section → `Settings/RagOptions.cs`):
- `Rag:UseInMemoryVectorStore` → `InMemoryVectorStore` (brute-force cosine, process-lifetime only) vs. `QdrantClient` (raw REST against Qdrant).
- `Rag:UseLocalAiFallback` → `LocalEmbeddingClient` (hashed bag-of-words vectors) + `LocalAnswerGenerator` (returns top overlapping chunks verbatim, no LLM) vs. `OllamaClient`, a single class implementing both `IEmbeddingClient` and `IAnswerGenerator` (`/api/embeddings`, `/api/generate`).

Note the defaults in `RagOptions` differ from `appsettings.json` (e.g. `UseLocalAiFallback` defaults to `true` in code but is `false` in appsettings; `ChunkSize` 100 vs 700), so behavior depends on which config is present. `EmbeddingDimensions` must match the embedding model's output (768 for `nomic-embed-text`) — Qdrant collections are created with this size, and startup fails with an explicit error if an existing collection's size differs.

**Ingestion is asynchronous:**
1. `IngestionController` (`/api/ingestion/scrape`, `/api/ingestion/upload`) validates input, saves uploads to `{DataDirectory}/inbox/`, and enqueues an `IngestionJob` on `IngestionQueue` (unbounded `Channel` + in-memory status map; finished statuses expire after `JobStatusRetentionMinutes`). Returns 202 with a job id; poll `GET /api/ingestion/{jobId}`.
2. `IngestionWorker` (hosted `BackgroundService`, processes jobs sequentially) extracts text via `WebScraperService` (AngleSharp, strips nav/script/etc.) or `FileTextExtractor` (OpenXml for docx, ClosedXML for xlsx), writes `{DataDirectory}/{jobId}_raw.txt`, deletes the uploaded file, chunks with `TextChunker` (fixed character windows with overlap), embeds each chunk, and upserts into the vector store. Chunk ids are a SHA-256 of the chunk text, so re-ingesting identical content overwrites rather than duplicates — both stores must keep upsert-by-id semantics.

`Rag:DataDirectory` is made absolute against the content root in `Program.cs` (`PostConfigure`); always read it from `RagOptions` rather than building paths from `ContentRootPath` or the CWD.

The worker's `ExecuteAsync` must not let exceptions escape — that stops the whole host. Vector store init is retried with backoff; per-job errors are caught and recorded as `Failed`.

**SSRF guard:** the scraper's `HttpClient` uses `UrlSafety.CreateHandler()`, whose `ConnectCallback` only connects to public IPs (covers redirects and DNS rebinding; proxies are disabled for it). Don't replace that handler or add a proxy to this client.

**Query path:** `ChatController` → `RagService.AskAsync`: embed question → vector search (`TopK`) → `IAnswerGenerator` with the retrieved chunks → `AskResponse(answer, context)`.

**State is not durable in default config:** job statuses and the in-memory vector store are lost on restart, and the raw text files in `data/` are not re-indexed at startup. Use Qdrant for persistence.

Other cross-cutting setup in `Program.cs`: a global fixed-window rate limiter (100 req / 10 s), `/health` health check, long HttpClient timeouts (Ollama 10 min, scraper 2 min). Supported upload types are enforced in both `IngestionController` and `FileTextExtractor` — update both when adding a format.
