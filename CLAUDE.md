# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

ASP.NET Core Web API (.NET 10, SDK pinned via `global.json`; single project `ChatBotRAGAPI/`, solution `ChatBotRAGAPI.slnx`) that implements a retrieval-augmented-generation chatbot: content is ingested from URLs or uploaded files, chunked, embedded, stored in a vector store, and then queried via `/api/chat/ask`. Tests live in `ChatBotRAGAPI.Tests/` (xUnit v3).

## Commands

Run from the repo root (or `ChatBotRAGAPI/`):

```bash
dotnet build ChatBotRAGAPI.slnx
dotnet test                                                 # all tests; needs no Ollama/Qdrant/network
dotnet test --filter-class ChatBotRAGAPI.Tests.TextChunkerTests      # one class (or --filter-method "*Name*")
dotnet run --project ChatBotRAGAPI --launch-profile http    # http://localhost:5170, Swagger at /swagger (Development only)
docker compose -f ChatBotRAGAPI/docker-compose.yml up -d    # Qdrant :6333 + Ollama :11434
ollama list                                                 # models must match Rag:EmbeddingModel / Rag:ChatModel
```

`appsettings.json` names the locally installed models `text-embedding-nomic:latest` (a nomic-embed-text build, 768 dims) and `gpt-5-qwen3:latest` (qwen3 8B). On a machine without them, pull e.g. `nomic-embed-text` and a chat model and override the names via config/env (`Rag__EmbeddingModel`, `Rag__ChatModel`); a different embedding model needs `EmbeddingDimensions` and `MinScore` recalibrated.

`global.json` opts `dotnet test` into Microsoft.Testing.Platform (required for xUnit v3 on the .NET 10 SDK), so use MTP-style filters, not `--filter`. Unit tests cover services with fakes/`StubHttpHandler`; `ApiTests` boot the real app through `RagApiFactory` (`WebApplicationFactory<Program>` with in-memory store + local AI fallback + temp `DataDirectory`, set via `UseSetting` so `Program.cs` sees them before backends are chosen).

Manual API testing: `ChatBotRAGAPI/ChatBotRAGAPI.http` and `ChatBotRAGAPI/ChatBotRAGAPI.postman_collection.json`. Sample ingestion inputs (txt/csv/docx/xlsx) live in `ChatBotRAGAPI/testdata/`.

## Architecture

**Pluggable backends chosen at startup** (`Program.cs`, driven by the `Rag` config section → `Settings/RagOptions.cs`):
- `Rag:UseInMemoryVectorStore` → `InMemoryVectorStore` (brute-force cosine, process-lifetime only) vs. `QdrantClient` (raw REST against Qdrant).
- `Rag:UseLocalAiFallback` → `LocalEmbeddingClient` (hashed bag-of-words vectors) + `LocalAnswerGenerator` (returns top overlapping chunks verbatim, no LLM) vs. `OllamaClient`, a single class implementing both `IEmbeddingClient` and `IAnswerGenerator` (batched `/api/embed`, `/api/generate`). `Rag:DisableThinking` sends `think: false` — without it, reasoning models such as qwen3 spend `MaxGenerateTokens` on hidden thinking and return empty or truncated answers.

Note the defaults in `RagOptions` differ from `appsettings.json` (e.g. `UseLocalAiFallback` defaults to `true` in code but is `false` in appsettings; `ChunkSize` 100 vs 700), so behavior depends on which config is present. `EmbeddingDimensions` must match the embedding model's output (768 for `nomic-embed-text`) — Qdrant collections are created with this size, and startup fails with an explicit error if an existing collection's size differs.

**Ingestion is asynchronous:**
1. `IngestionController` (`/api/ingestion/scrape`, `/api/ingestion/upload`) validates input, saves uploads to `{DataDirectory}/inbox/`, and enqueues an `IngestionJob` on `IngestionQueue` (unbounded `Channel` + in-memory status map; finished statuses expire after `JobStatusRetentionMinutes`). Returns 202 with a job id; poll `GET /api/ingestion/{jobId}`.
2. `IngestionWorker` (hosted `BackgroundService`, processes jobs sequentially) extracts text via `WebScraperService` (AngleSharp, strips nav/script/etc.) or `FileTextExtractor` (OpenXml for docx, ClosedXML for xlsx, PdfPig for pdf), deletes the uploaded file, saves the text via `DocumentRegistry`, chunks with `TextChunker`, then embeds and upserts in batches of `EmbeddingBatchSize`.

**Documents are keyed by source** (original file name or URL, never the GUID inbox path); the source is stored on every chunk and becomes the citation. `DocumentRegistry` persists `{DataDirectory}/documents/index.json` (source → type, ingestedAt, chunk ids) plus each document's text as `{sha256(source)}.txt`. Re-ingesting a source replaces it: unchanged chunks are overwritten in place and chunks no longer present are deleted. Chunk ids are SHA-256 of `source + \0 + chunk text`, so identical text in two documents gives separate points and deleting one can't touch the other; both stores must keep upsert-by-id and delete-by-id semantics. Commits to the store+registry (worker) and deletes (`DocumentsController`: `GET /api/documents`, `DELETE /api/documents?source=…`) are serialized with `DocumentRegistry.LockAsync`.

**Re-index at startup:** when `IVectorStore.IsPersistent` is false (in-memory), the worker enqueues a `reindex` job per registered document, which re-chunks/re-embeds the stored text through the normal pipeline (so it picks up changed chunk settings and shows up in job status).

`Rag:DataDirectory` is made absolute against the content root in `Program.cs` (`PostConfigure`); always read it from `RagOptions` rather than building paths from `ContentRootPath` or the CWD.

The worker's `ExecuteAsync` must not let exceptions escape — that stops the whole host. Vector store init is retried with backoff; per-job errors are caught and recorded as `Failed`.

**SSRF guard:** the scraper's `HttpClient` uses `UrlSafety.CreateHandler()`, whose `ConnectCallback` only connects to public IPs (covers redirects and DNS rebinding; proxies are disabled for it). Don't replace that handler or add a proxy to this client.

**Chunking:** `TextChunker` splits recursively on paragraph → line → sentence → word boundaries (hard cut only for a single over-long word), packs pieces up to `ChunkSize` chars, and overlaps with whole trailing pieces or, failing that, trailing words. It depends on extractors preserving structure: `FileTextExtractor` emits one line per docx paragraph / xlsx row, and `WebScraperService` inserts newlines at HTML block boundaries, and PDFs are extracted per page with `ContentOrderTextExtractor` keeping lines (pages separated by a blank line; image-only PDFs fail the job since there is no OCR; tightly justified PDFs, e.g. LaTeX papers, can lose a few inter-word spaces) — don't collapse newlines in extractors.

**Query path:** `ChatController` → `RagService.AskAsync`: embed question → vector search (`TopK`) → drop hits below `EffectiveMinScore` (`MinScore` for Ollama, `LocalMinScore` for the local fallback; if none remain, return "I don't have enough data." without calling the generator) → `IAnswerGenerator` with numbered passages → `AskResponse(answer, context, sources)`. Passage *i* is cited as `[i+1]` and matches `sources[i].id`. `MinScore` is embedding-model-specific (0.45 was calibrated for nomic-embed-text: relevant hits scored 0.56–0.75, off-topic ≤0.41); recalibrate when changing `EmbeddingModel`. The local fallback's scores are on a much lower scale, hence the separate `LocalMinScore` (0.08); it tokenizes via `LocalText` (stopwords dropped, so sharing only "the"/"is" is not a match). Its hashed vectors can still collide, so `LocalAnswerGenerator` additionally requires a real shared term before answering.

**What survives a restart:** documents do (registry on disk; the in-memory store is rebuilt from it, which needs the embedding backend up — failures show as `Failed` reindex jobs). Job statuses do not. With Qdrant, the registry and collection must stay together: pointing at a different/emptied collection or data directory leaves them out of sync. Old `{jobId}_raw.txt` files from before the registry are not imported.

Other cross-cutting setup in `Program.cs`: a global fixed-window rate limiter (100 req / 10 s), `/health` health check, long HttpClient timeouts (Ollama 10 min, scraper 2 min). Supported upload types live in one place, `FileTextExtractor.SupportedExtensions` (the controller validates against it); add the format's case to `ExtractAsync` alongside it.
