using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
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

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
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
builder.Services.AddHealthChecks();
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
    builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();
}
else
{
    builder.Services.AddHttpClient<IVectorStore, QdrantClient>().AddBackendRetries(ragOptions);
}

// AI layer
if (ragOptions.UseLocalAiFallback)
{
    builder.Services.AddSingleton<IEmbeddingClient, LocalEmbeddingClient>();
    builder.Services.AddSingleton<IAnswerGenerator, LocalAnswerGenerator>();
}
else
{
    builder.Services.AddHttpClient<OllamaClient>(client =>
    {
        client.BaseAddress = new Uri(ragOptions.OllamaBaseUrl);
        client.Timeout = TimeSpan.FromMinutes(10); // important
    }).AddBackendRetries(ragOptions);

    builder.Services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<OllamaClient>());
    builder.Services.AddSingleton<IAnswerGenerator>(sp => sp.GetRequiredService<OllamaClient>());
}

builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddSingleton<DocumentRegistry>();
builder.Services.AddSingleton<FileTextExtractor>();
builder.Services.AddSingleton<TextChunker>();
builder.Services.AddSingleton<RagService>();
builder.Services.AddHostedService<IngestionWorker>();

var app = builder.Build();

if (string.IsNullOrEmpty(app.Services.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey))
    app.Logger.LogWarning(
        "Security:ApiKey is not set: ingestion and document endpoints are open to anyone who can reach this app.");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapControllers();

app.Run();