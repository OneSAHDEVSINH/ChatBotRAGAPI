# ChatBotRAGAPI

A retrieval-augmented generation (RAG) chatbot API on ASP.NET Core (.NET 10). Ingest web pages and documents,
then ask questions that are answered **only** from that content, with numbered citations back to the source.

- **Ingest** web pages (or crawl a whole site) and files: `.txt`, `.csv`, `.docx`, `.xlsx`, `.pdf`
- **Hybrid search**: vector similarity fused with BM25 keyword search, plus a relevance threshold so off-topic questions get *"I don't have enough data."* instead of a hallucination
- **Cited answers**: `[1]`, `[2]` in the answer match `sources[].id`
- **Streaming** answers over Server-Sent Events, and **follow-up questions** via conversation history
- **Documents** are tracked by source: re-ingesting replaces, deleting removes every chunk, and the index survives restarts
- Runs fully local with [Ollama](https://ollama.com); a no-LLM fallback mode needs nothing else at all

## Quick start

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for real answers, Ollama.

```bash
# 1. Models (any embedding + chat model works; see Configuration)
ollama pull nomic-embed-text
ollama pull qwen3:8b

# 2. Run (Swagger UI at http://localhost:5170/swagger in Development)
dotnet run --project ChatBotRAGAPI --launch-profile http
```

Point the app at the models you pulled, e.g. `Rag__EmbeddingModel=nomic-embed-text` and `Rag__ChatModel=qwen3:8b`
as environment variables, or edit `ChatBotRAGAPI/appsettings.json`.

**No Ollama?** Set `Rag__UseLocalAiFallback=true`: hashed bag-of-words embeddings and extractive answers (it returns the
matching passages instead of writing an answer). Good for trying the API and for tests.

**Qdrant** (optional, persistent vectors): `docker compose -f ChatBotRAGAPI/docker-compose.yml up -d qdrant` and set
`Rag__UseInMemoryVectorStore=false`. Without it an in-memory store is used and rebuilt from stored documents at startup.

## Using the API

```bash
# Ingest a file or a web page (returns a jobId; ingestion runs in the background)
curl -F "file=@ChatBotRAGAPI/testdata/company_profile.txt" http://localhost:5170/api/ingestion/upload
curl -X POST http://localhost:5170/api/ingestion/scrape -H "Content-Type: application/json" -d '{"url":"https://example.com/"}'
curl -X POST http://localhost:5170/api/ingestion/scrape -H "Content-Type: application/json" -d '{"url":"https://example.com/","maxPages":20}'  # crawl
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
| `GET /api/documents` | List ingested documents (source, type, chunk count, time) |
| `DELETE /api/documents?source=...` | Remove a document and all its chunks |
| `POST /api/chat/ask` | Answer a question `{ "question": "...", "history": [...] }` |
| `POST /api/chat/ask/stream` | Same, as Server-Sent Events: `sources`, `delta`..., `done` (or `error`) |
| `GET /health` | Health check |

- **Follow-ups**: the server keeps no conversation state. Send earlier turns as
  `"history": [{"role":"user","content":"..."},{"role":"assistant","content":"..."}]`.
- **Documents are keyed by source** (file name or URL): uploading `report.pdf` again replaces the previous `report.pdf`.
- **Crawling** (`maxPages` > 1, up to `Rag:MaxCrawlPages`) follows links on the same host and under the start page's
  path (a home page covers the whole site; `/docs/intro` covers `/docs/...`), breadth-first, honouring robots.txt and
  pausing `CrawlDelayMilliseconds` between requests. Each page becomes its own document named by its URL, and the job
  message says how many pages were ingested. A single page often lacks facts that live elsewhere on the site (the
  founding year on the About page, say), so crawl when a site is the source.
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
| `EmbeddingModel` / `ChatModel` | `text-embedding-nomic:latest` / `gpt-5-qwen3:latest` | Ollama model names (these are local builds; use your own) |
| `EmbeddingDimensions` | 768 | Must match the embedding model (Qdrant collection size) |
| `MinScore` | 0.45 | Relevance cut-off, calibrated for nomic-embed-text; **recalibrate for other embedding models** |
| `TopK` | 3 | Passages given to the model |
| `ChunkSize` / `ChunkOverlap` | 700 / 80 | Characters; chunks split on paragraph → line → sentence → word |
| `HybridSearch` | true | Add BM25 keyword search to vector search |
| `MaxHistoryMessages` | 6 | Conversation turns used for follow-ups |
| `DisableThinking` | true | Stops reasoning models (qwen3, deepseek-r1) spending the token budget on hidden thinking |
| `HttpRetryAttempts` | 2 | Retries of transient Ollama/Qdrant failures; outages return 503 |
| `MaxCrawlPages` / `CrawlDelayMilliseconds` | 50 / 500 | Largest allowed `maxPages`; pause between crawled pages |

Changing the chunking settings or the embedding model means re-ingesting (or restarting, for the in-memory store).

## Development

```bash
dotnet build ChatBotRAGAPI.slnx
dotnet test                                                      # no Ollama, Qdrant or network needed
dotnet test --filter-class ChatBotRAGAPI.Tests.TextChunkerTests  # one class
```

Tests use xUnit v3 on Microsoft.Testing.Platform (enabled in `global.json`) and run on every push via GitHub Actions.
Architecture notes for contributors are in [CLAUDE.md](CLAUDE.md).

## Limitations

- Scanned (image-only) PDFs are rejected: there is no OCR.
- Crawling doesn't remove pages that have disappeared from the site; delete them via `DELETE /api/documents`.
  Pages rendered by JavaScript only are seen as empty (no browser is used). Jobs run one at a time, so a large crawl
  delays uploads queued after it.
- Keyword search has no stemming ("cost" does not match "costs"); vector search usually covers it.
- On CPU, an 8B chat model takes 10–60 s per answer; streaming shows text as it is generated.
