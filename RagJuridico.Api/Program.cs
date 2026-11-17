using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Qdrant.Client;
using RagJuridico.Api.Options;
using RagJuridico.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));

// Qdrant (gRPC)
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
    return new QdrantClient(o.Host, o.Port);
});

// Ollama — programamos contra as abstrações do Microsoft.Extensions.AI,
// então trocar de provedor (ex.: Claude) depois é só mudar este registro.
static OllamaApiClient CreateOllama(OllamaOptions o, string model) =>
    new(new HttpClient { BaseAddress = new Uri(o.Url), Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds) }, model);

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var o = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    return CreateOllama(o, o.ChatModel);
});
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
{
    var o = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    return CreateOllama(o, o.EmbeddingModel);
});

builder.Services.AddSingleton<VectorStore>();
builder.Services.AddSingleton<IngestionService>();
builder.Services.AddSingleton<RagChatService>();

// Frontend React (Vite) em dev
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api");

api.MapPost("/documentos", async (IFormFile arquivo, IngestionService ingestion, CancellationToken ct) =>
{
    try
    {
        await using var stream = arquivo.OpenReadStream();
        var result = await ingestion.IngestAsync(stream, arquivo.FileName, ct);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
    {
        return Results.BadRequest(new { erro = ex.Message });
    }
    catch (HttpRequestException ex)
    {
        return OllamaOffline(ex, app.Configuration);
    }
})
.DisableAntiforgery(); // upload multipart sem token antiforgery (API, não formulário server-side)

// Mostra como o arquivo seria dividido, sem indexar nada. Útil para estudar/ajustar o chunking.
api.MapPost("/documentos/preview", async (IFormFile arquivo, IOptions<RagOptions> rag, CancellationToken ct) =>
{
    try
    {
        using var buffer = new MemoryStream();
        await arquivo.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var pages = DocumentTextExtractor.Extract(buffer, arquivo.FileName);
        var chunks = TextChunker.Split(pages, rag.Value.ChunkSize, rag.Value.ChunkOverlap)
            .Select((c, i) => new { trecho = i + 1, pagina = c.Page, caracteres = c.Text.Length, texto = c.Text })
            .ToList();
        return Results.Ok(new { arquivo = arquivo.FileName, paginas = pages.Count, trechos = chunks });
    }
    catch (NotSupportedException ex)
    {
        return Results.BadRequest(new { erro = ex.Message });
    }
})
.DisableAntiforgery();

api.MapGet("/documentos", (VectorStore store, CancellationToken ct) => store.ListDocumentsAsync(ct));

api.MapDelete("/documentos/{id}", async (string id, VectorStore store, CancellationToken ct) =>
{
    await store.DeleteDocumentAsync(id, ct);
    return Results.NoContent();
});

api.MapPost("/chat", async (ChatRequest req, RagChatService rag, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Pergunta))
        return Results.BadRequest(new { erro = "Informe a pergunta." });
    try
    {
        return Results.Ok(await rag.AskAsync(req.Pergunta, ct));
    }
    catch (HttpRequestException ex)
    {
        return OllamaOffline(ex, app.Configuration);
    }
});

app.Run();

static IResult OllamaOffline(HttpRequestException ex, IConfiguration config) => Results.Problem(
    title: "Ollama indisponível",
    detail: $"Não consegui falar com o Ollama em {config["Ollama:Url"]}. Ele está rodando e com os modelos baixados? ({ex.Message})",
    statusCode: StatusCodes.Status503ServiceUnavailable);

public sealed record ChatRequest(string Pergunta);
