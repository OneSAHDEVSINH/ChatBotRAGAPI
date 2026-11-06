# ChatBotRAGAPI

A retrieval-augmented generation (RAG) chatbot API on ASP.NET Core (.NET 10). Ingest web pages and documents,
then ask questions that are answered **only** from that content, with numbered citations back to the source.

- **Ingest** web pages (or crawl a whole site) and files: `.txt`, `.csv`, `.docx`, `.xlsx`, `.pdf`
- **Hybrid search**: vector similarity fused with BM25 keyword search, plus a relevance threshold so off-topic questions get *"I don't have enough data."* instead of a hallucination
- **Cited answers**: `[1]`, `[2]` in the answer match `sources[].id`
- **Streaming** answers over Server-Sent Events, and **follow-up questions** via conversation history
- **Documents** are tracked by source: re-ingesting replaces, deleting removes every chunk, and the index survives restarts; embeddings are cached on disk, so restarts and unchanged re-ingests don't re-embed
- **Chat UI** at `/`: streaming answers, clickable citations that show the passage, follow-up questions
- Runs fully local with [Ollama](https://ollama.com); a no-LLM fallback mode needs nothing else at all

## How it works

[![Architecture: chat UI and clients call the chat API, whose RagService runs hybrid search over a vector store and a BM25 keyword index and asks Ollama for a cited answer; uploads and crawled pages go through the ingestion API and worker, which chunk, embed and store them](docs/architecture.png)](docs/architecture.html)

Questions run along the top: the chat API hands them to `RagService`, which searches by meaning (vectors) and by
keywords (BM25), keeps only passages above `MinScore`, and has Ollama answer from those with citations. Content
enters along the bottom: uploads and crawled pages are queued, then extracted, chunked, embedded and stored.

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for real answers, Ollama.

```bash
# 1. Models (any embedding + chat model works; see Configuration)
ollama pull nomic-embed-text
ollama pull qwen3:8b

# 2. Run: chat UI at http://localhost:5170/, Swagger at /swagger (Development)
dotnet run --project ChatBotRAGAPI --launch-profile http
```

Point the app at the models you pulled, e.g. `Rag__EmbeddingModel=nomic-embed-text` and `Rag__ChatModel=qwen3:8b`
as environment variables, or edit `ChatBotRAGAPI/appsettings.json`.

**No Ollama?** Set `Rag__UseLocalAiFallback=true`: hashed bag-of-words embeddings and extractive answers (it returns the
matching passages instead of writing an answer). Good for trying the API and for tests.

**OpenAI, Azure OpenAI or Claude instead of Ollama** (via Microsoft.Extensions.AI): pick the providers and models with
environment variables; keys go in the environment or user secrets, never in `appsettings.json`.

```bash
# Claude answers, Ollama keeps embedding (Anthropic has no embeddings API)
Rag__ChatProvider=Anthropic  Rag__ChatModel=claude-opus-5-5  ANTHROPIC_API_KEY=...

# OpenAI for both (a different embedding model: set its size and recalibrate MinScore)
Rag__ChatProvider=OpenAI  Rag__ChatModel=gpt-5  Rag__EmbeddingProvider=OpenAI Rag__EmbeddingModel=text-embedding-3-small  Rag__EmbeddingDimensions=1536  OPENAI_API_KEY=...

# Azure OpenAI, or any OpenAI-compatible server: add the endpoint
Rag__OpenAI__Endpoint=https://<resource>.openai.azure.com/openai/v1/  Rag__OpenAI__ApiKey=...
```

**Qdrant** (optional, persistent vectors): `docker compose -f ChatBotRAGAPI/docker-compose.yml up -d qdrant` and set
`Rag__UseInMemoryVectorStore=false`. Without it an in-memory store is used and rebuilt from stored documents at startup.

## Using the API

```bash
# Ingest a file or a web page (returns a jobId; ingestion runs in the background)
curl -F "file=@ChatBotRAGAPI/testdata/company_profile.txt" http://localhost:5170/api/ingestion/upload
curl -X POST http://localhost:5170/api/ingestion/scrape -H "Content-Type: application/json" -d '{"url":"https://example.com/"}'
curl -X POST http://localhost:5170/api/ingestion/scrape -H "Content-Type: application/json" -d '{"url":"https://example.com/","maxPages":20}'  # crawl
curl -X POST http://localhost:5170/api/ingestion/scrape -H "Content-Type: application/json" -d '{"url":"https://example.com/","maxPages":20,"removeMissing":true}'  # re-crawl, drop pages that are gone
curl http://localhost:5170/api/ingestion/{jobId}          # Queued | Running | Completed | Failed

# Ask
curl -X POST http://localhost:5170/api/chat/ask -H "Content-Type: application/json" \
     -d '{"question":"When was the company founded?"}'
```

```json
{
  "answer": "The company was founded in 2016 [1].",
  "context": ["Giriraj Digital - Internal Profile\n\nFounded: 2016 ..."],
  "sources": [{ "id": 1, "source": "company_profile.txt", "score": 0.5652, "match": "both" }]
}
```

| Endpoint | Purpose |
|---|---|
| `POST /api/ingestion/upload` | Upload a file (multipart field `file`) |
| `POST /api/ingestion/scrape` | Ingest a public web page `{ "url": "..." }`, or crawl the site from it with `"maxPages": n` |
| `GET /api/ingestion/{jobId}` | Ingestion job status |
| `GET /api/ingestion/jobs` | Recent ingestion jobs (statuses survive restarts; interrupted jobs read Failed) |
| `GET /api/documents` | List ingested documents (source, type, chunk count, time) |
| `DELETE /api/documents?source=...` | Remove a document and all its chunks |
| `POST /api/chat/ask` | Answer a question `{ "question": "...", "history": [...] }` |
| `POST /api/chat/ask/stream` | Same, as Server-Sent Events: `sources`, `context`, `delta`..., `done` (or `error`) |
| `GET /health` | Liveness: the process answers (for restarts) |
| `GET /health/ready` | Readiness: vector store reachable, models pulled (Ollama), worker started; JSON per check, 503 when not ready |
| `GET /` | Chat UI (`wwwroot/`) |

- **Follow-ups**: the server keeps no conversation state. Send earlier turns as
  `"history": [{"role":"user","content":"..."},{"role":"assistant","content":"..."}]`.
- **Documents are keyed by source** (file name or URL): uploading `report.pdf` again replaces the previous `report.pdf`.
- **Crawling** (`maxPages` > 1, up to `Rag:MaxCrawlPages`) follows links on the same host and under the start page's
  path (a home page covers the whole site; `/docs/intro` covers `/docs/...`), breadth-first, honouring robots.txt and
  pausing `CrawlDelayMilliseconds` between requests. Each page becomes its own document named by its URL, and the job
  message says how many pages were ingested. A single page often lacks facts that live elsewhere on the site (the
  founding year on the About page, say), so crawl when a site is the source. Add `"removeMissing": true` to a re-crawl
  to delete that site's pages the crawl no longer finds (unlinked, or 404/410); nothing is removed if the crawl stopped
  at `maxPages`, and pages failing with a temporary error are kept.
- `sources[].match` says how a passage was found (`semantic`, `keyword`, `both`); `score` is the cosine similarity
  (null for keyword-only matches).
- **Swagger** describes every endpoint and field and pre-fills realistic examples (the drop-down offers more, e.g. a
  follow-up with history or a crawl). Leave `history` out for a first question.
- Ready-made requests: `ChatBotRAGAPI/ChatBotRAGAPI.http` (VS / VS Code / Rider) and
  `ChatBotRAGAPI/ChatBotRAGAPI.postman_collection.json` (Postman: set `baseUrl` and `apiKey`).

## Security

- **API key**: set `Security__ApiKey` (environment or user secrets, never commit it) to require an `X-Api-Key` header on
  ingestion and document endpoints. Chat and health stay open for end users. Without a key those endpoints are open,
  and a warning is logged at startup.
- **Rate limiting** per client IP (`RateLimit` section; default 100 requests / 10 s).
- **Behind a reverse proxy** (nginx, Apache, a load balancer) every request comes from the proxy, so all
  clients would share one rate limit. Set `ForwardedHeaders__Enabled=true`: `X-Forwarded-For`/`-Proto` are then used,
  but only from loopback and the proxies in `ForwardedHeaders:KnownProxies` (IPs) / `KnownNetworks` (CIDR), so clients
  can't fake their address. Hosting in IIS doesn't need this: the IIS integration already passes the client address.
- **Scraping is SSRF-protected**: only public addresses are fetched, checked at connect time (covers redirects and DNS
  rebinding).

## Configuration

All under `Rag` in `ChatBotRAGAPI/appsettings.json` (override with env vars such as `Rag__ChatModel`). The important ones:

| Setting | Default | Notes |
|---|---|---|
| `ChatProvider` / `EmbeddingProvider` | `Ollama` / `Ollama` | `Ollama`, `OpenAI` (also Azure OpenAI and OpenAI-compatible servers via `OpenAI:Endpoint`) or `Anthropic` (chat only) |
| `EmbeddingModel` / `ChatModel` | `text-embedding-nomic:latest` / `gpt-5-qwen3:latest` | Model names for those providers (the defaults are local Ollama builds; use your own) |
| `OpenAI:MaxOutputTokens` / `Anthropic:MaxTokens` | 16000 | Answer budget for cloud models, which count their reasoning in it (`MaxGenerateTokens` is Ollama's) |
| `EmbeddingDimensions` | 768 | Must match the embedding model (Qdrant collection size) |
| `MinScore` | 0.45 | Relevance cut-off, calibrated for nomic-embed-text; **recalibrate for other embedding models** |
| `TopK` | 3 | Passages given to the model |
| `ChunkSize` / `ChunkOverlap` | 700 / 80 | Characters; chunks split on paragraph → line → sentence → word |
| `HybridSearch` | true | Add BM25 keyword search to vector search |
| `MaxHistoryMessages` | 6 | Conversation turns used for follow-ups |
| `DisableThinking` | true | Stops reasoning models (qwen3, deepseek-r1) spending the token budget on hidden thinking |
| `OcrModel` | empty (off) | Ollama vision model that reads scanned PDF pages; `OcrMaxTokens` (2048) caps one page |
| `HttpRetryAttempts` | 2 | Retries of transient Ollama/Qdrant failures; outages return 503 |
| `MaxCrawlPages` / `CrawlDelayMilliseconds` | 50 / 500 | Largest allowed `maxPages`; pause between crawled pages |
| `PrioritizeChat` / `ChatPriorityMaxWaitSeconds` | true / 30 | Ingestion pauses between embedding batches while a question is being answered (at most this long per batch) |

Changing the chunking settings or the embedding model means re-ingesting (or restarting, for the in-memory store).
Embeddings are cached per document in `data/documents/*.vec`, so only chunks whose text changed are embedded again; a
different embedding model ignores the cache and re-embeds everything.

## Development

```bash
dotnet build ChatBotRAGAPI.slnx
dotnet test                                                      # no Ollama or network needed; real-Qdrant tests run when Docker is available
dotnet test --filter-class ChatBotRAGAPI.Tests.TextChunkerTests  # one class
```

Tests use xUnit v3 on Microsoft.Testing.Platform (enabled in `global.json`) and run on every push via GitHub Actions.
Architecture notes for contributors are in [CLAUDE.md](CLAUDE.md).

## Limitations

- Scanned PDFs need a vision model: set `Rag__OcrModel` (e.g. `qwen2.5vl:3b`, pulled into Ollama) and pages with no text
  layer are read by OCR, one model call per page (slow on CPU). Without it they are rejected. Page images stored as
  JBIG2, CCITT or JPEG 2000 cannot be sent to the model and are reported.
  Pages rendered by JavaScript only are seen as empty (no browser is used). Jobs run one at a time, so a large crawl
  delays uploads queued after it.
- Keyword search stems English words (Porter), so "costs" matches "cost"; other languages get exact-word matching.
- On CPU, an 8B chat model takes 10–60 s per answer; streaming shows text as it is generated.
