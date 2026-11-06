# Deploying ChatBotRAGAPI

Three ways to run it outside a development machine. Whatever the host, the same four things decide whether it works:

1. **Persistent storage for `Rag:DataDirectory`** (default `data/` beside the app). It holds the document registry and texts, the
   embedding cache, the job list and in-flight uploads. Lose it and every document is gone.
2. **An API key** in `Security__ApiKey` (environment, never `appsettings.json`). Without one, anyone who can reach the app can
   ingest and delete documents.
3. **Reachable model and vector backends**: Ollama and/or Qdrant, or a cloud provider (`Rag__ChatProvider=OpenAI|Anthropic`).
4. **A readiness probe on `/health/ready`** (503 until the vector store answers and the configured models are available) and a
   liveness probe on `/health`.

Configuration is the `Rag`, `Security`, `RateLimit` and `ForwardedHeaders` sections of `appsettings.json`; every value can be set
as an environment variable with `__` for each level, e.g. `Rag__ChatModel`.

## Docker Compose (API + Qdrant + Ollama)

```bash
export API_KEY=change-me                                   # PowerShell: $env:API_KEY = "change-me"
docker compose -f ChatBotRAGAPI/docker-compose.yml up -d --build
docker compose -f ChatBotRAGAPI/docker-compose.yml exec ollama ollama pull nomic-embed-text
docker compose -f ChatBotRAGAPI/docker-compose.yml exec ollama ollama pull qwen3:8b
curl http://localhost:8080/health/ready                    # "Healthy" once the models are pulled
```

The chat UI is at `http://localhost:8080/`, the admin page at `http://localhost:8080/admin.html`. Documents and jobs live in the
`api_data` volume, vectors in `qdrant_data`, models in `ollama_data`. Other models: set `CHAT_MODEL` / `EMBEDDING_MODEL` before
`up` (a different embedding model also needs `Rag__EmbeddingDimensions` and a recalibrated `Rag__MinScore`; see
`POST /api/eval/calibrate`). On CPU, an 8B chat model answers in 10 to 60 seconds; give Ollama a GPU if you can
(`deploy.resources.reservations.devices` in the compose file).

The image alone: `docker build -t chatbotragapi .` from the repository root, then
`docker run -p 8080:8080 -v ragdata:/app/data -e Security__ApiKey=... -e Rag__OllamaBaseUrl=http://<host>:11434 chatbotragapi`.
It runs as a non-root user and listens on 8080.

## IIS (Windows Server)

1. Install the [.NET 10 Hosting Bundle](https://dotnet.microsoft.com/download/dotnet/10.0) on the server, then restart IIS
   (`iisreset`).
2. Publish: `dotnet publish ChatBotRAGAPI -c Release -o C:\inetpub\chatbotragapi`.
3. Create a site (or application) pointing at that folder, with an application pool set to **No Managed Code**. The app runs
   in-process.
4. Give the pool identity (`IIS AppPool\<pool name>`) **Modify** rights on the data folder. Either let it create
   `C:\inetpub\chatbotragapi\data`, or keep data outside the web root and set `Rag__DataDirectory=D:\chatbotragapi-data`.
5. Set environment variables on the application pool (IIS Manager → Configuration Editor →
   `system.applicationHost/applicationPools` → environmentVariables) or in the published `web.config`
   (`<aspNetCore><environmentVariables>`): `Security__ApiKey`, `Rag__OllamaBaseUrl`, `Rag__QdrantBaseUrl`, and so on. Keep keys
   out of `web.config` if it is checked in anywhere.
6. Set the pool's **Idle Time-out** to 0 and **Start Mode** to AlwaysRunning (with the Application Initialization feature).
   Otherwise IIS stops the app when idle, which stops ingestion jobs and, with the in-memory store, forces a re-index on the
   next request.

IIS passes the real client address through to the app, so `ForwardedHeaders` stays off. Add HTTPS with an IIS binding.

## Azure App Service

Running Ollama inside App Service isn't practical, so use a cloud provider for answers and embeddings, and either Qdrant
(Qdrant Cloud or your own VM/container) or the in-memory store:

| App setting | Value |
|---|---|
| `Security__ApiKey` | your key (or a Key Vault reference) |
| `Rag__ChatProvider` / `Rag__ChatModel` | `OpenAI` + a deployment name (Azure OpenAI), or `Anthropic` + `claude-opus-5-5` |
| `Rag__OpenAI__Endpoint` / `Rag__OpenAI__ApiKey` | `https://<resource>.openai.azure.com/openai/v1/` and its key (Azure OpenAI) |
| `Rag__EmbeddingProvider` / `Rag__EmbeddingModel` / `Rag__EmbeddingDimensions` | `OpenAI`, e.g. `text-embedding-3-small`, `1536` |
| `Rag__MinScore` | recalibrated for that embedding model (`POST /api/eval/calibrate`) |
| `Rag__DataDirectory` | `/home/data` on Linux (persistent `/home` share), or `D:\home\data` on Windows |
| `Rag__UseInMemoryVectorStore` / `Rag__QdrantBaseUrl` | `false` + your Qdrant URL, or leave the in-memory store on |
| `ANTHROPIC_API_KEY` | when the chat provider is Anthropic |

Deploy the code (`az webapp deploy`, or a GitHub Actions workflow) or the container image above (set `WEBSITES_PORT=8080`).
Under **Monitoring → Health check**, set the path to `/health/ready`. Turn **Always On** on, so the background worker isn't
unloaded.

App Service puts its own front ends in front of the app, so every request arrives from their addresses. If per-client rate
limiting matters, set `ForwardedHeaders__Enabled=true` and list the front ends' network in `ForwardedHeaders__KnownNetworks__0`.
Check the client addresses in the logs after enabling it, since a wrong network means the headers are ignored.

## After any deployment

- `GET /health/ready` answers 200 and every check is Healthy.
- `GET /api/documents` answers 401 without `X-Api-Key` and 200 with it.
- Upload one file on the admin page, wait for Completed, then ask a question about it in the chat UI.
