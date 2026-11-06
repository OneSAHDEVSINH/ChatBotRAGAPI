using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Services.Ai;
using ChatBotRAGAPI.Services.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ChatBotRAGAPI.Settings;
using ChatBotRAGAPI.Swagger;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));
// Resolve DataDirectory against the content root (not the process CWD) so uploads and the worker agree on one location.
builder.Services.PostConfigure<RagOptions>(o =>
    o.DataDirectory = Path.GetFullPath(o.DataDirectory, builder.Environment.ContentRootPath));
var ragOptions = builder.Configuration.GetSection("Rag").Get<RagOptions>() ?? new RagOptions();

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
var rateLimitOptions = builder.Configuration.GetSection("RateLimit").Get<RateLimitOptions>() ?? new RateLimitOptions();
var proxyOptions = builder.Configuration.GetSection("ForwardedHeaders").Get<ProxyOptions>() ?? new ProxyOptions();
if (proxyOptions.Enabled)
    builder.Services.Configure<ForwardedHeadersOptions>(o => ProxySetup.Configure(o, proxyOptions));

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    // Endpoint summaries and field descriptions come from the XML doc comments.
    c.IncludeXmlComments(typeof(Program).Assembly, includeControllerXmlComments: true);
    c.OperationFilter<RequestExamplesFilter>();

    // Lets Swagger UI send the key ("Authorize" button) to the endpoints marked [ApiKey].
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = ApiKeyAttribute.HeaderName,
        Description = "Required for ingestion and document endpoints when Security:ApiKey is set."
    });
    c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
    });
});
// /health is liveness (the process answers); /health/ready checks what a request needs (see Services/Health).
builder.Services.AddSingleton<WorkerState>();
builder.Services.AddHttpClient(AiBackendHealthCheck.HttpClientName, client =>
{
    client.BaseAddress = new Uri(ragOptions.OllamaBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHealthChecks()
    .AddCheck<VectorStoreHealthCheck>("vector-store", tags: ["ready"])
    .AddCheck<AiBackendHealthCheck>("ai-backend", tags: ["ready"])
    .AddCheck<IngestionWorkerHealthCheck>("ingestion-worker", tags: ["ready"]);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<BackendUnavailableHandler>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = ClientRateLimiter.Create(rateLimitOptions);
});

builder.Services.AddHttpClient<WebScraperService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ChatBotRAGAPI/1.0 (+https://localhost)");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
})
.ConfigurePrimaryHttpMessageHandler(UrlSafety.CreateHandler); // SSRF guard: public addresses only
// Vector store
if (ragOptions.UseInMemoryVectorStore)
{
    builder.Services.AddSingleton<InMemoryVectorStore>();
}
else
{
    builder.Services.AddHttpClient<QdrantClient>().AddBackendRetries(ragOptions);
}

// Everything goes through the decorator so the keyword index (hybrid search) mirrors the store.
builder.Services.AddSingleton<KeywordIndex>();
builder.Services.AddSingleton<IVectorStore>(sp => new KeywordIndexingVectorStore(
    ragOptions.UseInMemoryVectorStore
        ? sp.GetRequiredService<InMemoryVectorStore>()
        : sp.GetRequiredService<QdrantClient>(),
    sp.GetRequiredService<KeywordIndex>()));

// AI layer: the no-LLM fallback, or the providers named by Rag:ChatProvider / Rag:EmbeddingProvider.
if (ragOptions.UseLocalAiFallback)
{
    builder.Services.AddSingleton<IEmbeddingClient, LocalEmbeddingClient>();
    builder.Services.AddSingleton<IAnswerGenerator, LocalAnswerGenerator>();
}
else
{
    AiProviders.Register(builder.Services, ragOptions);
}

builder.Services.AddTransient<WebCrawler>();
builder.Services.AddSingleton<ChatActivity>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddSingleton<DocumentRegistry>();
builder.Services.AddSingleton<FileTextExtractor>();
builder.Services.AddSingleton<TextChunker>();
builder.Services.AddSingleton<RagService>();
builder.Services.AddSingleton<EvalService>();
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

if (string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey))
    app.Logger.LogWarning(
        "Security:ApiKey is not set: ingestion and document endpoints are open to anyone who can reach this app.");

// First, so the rate limiter and everything else see the real client address and scheme.
if (proxyOptions.Enabled)
    app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();

// Chat UI (wwwroot/index.html at "/"). It only calls the open chat endpoint, so it adds no exposure.
// The strict CSP is safe because the UI has no inline scripts or styles; it covers static files only
// (Swagger UI needs inline scripts).
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        headers.ContentSecurityPolicy =
            "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        headers.XContentTypeOptions = "nosniff";
        // Revalidate on every load (a cheap 304 via the ETag), so a deploy never runs an old app.js against new HTML.
        headers.CacheControl = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
    }
});
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthReportWriter.WriteAsync,
});
app.MapControllers();

app.Run();