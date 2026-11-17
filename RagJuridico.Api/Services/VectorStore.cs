using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using RagJuridico.Api.Options;
using static Qdrant.Client.Grpc.Conditions;

namespace RagJuridico.Api.Services;

public sealed record StoredDocument(string Id, string Arquivo, int Trechos);

/// <summary>Acesso ao Qdrant: cria a collection, grava, busca e remove trechos.</summary>
public sealed class VectorStore(QdrantClient client, IOptions<QdrantOptions> options, ILogger<VectorStore> logger)
{
    // Nomes dos campos gravados no payload de cada ponto.
    public const string FieldDocumentId = "documento_id";
    public const string FieldFile = "arquivo";
    public const string FieldPage = "pagina";
    public const string FieldText = "texto";

    private readonly QdrantOptions _opt = options.Value;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            if (!await client.CollectionExistsAsync(_opt.Collection, ct))
            {
                logger.LogInformation("Criando collection {Collection} ({Size} dimensões, cosseno)",
                    _opt.Collection, _opt.VectorSize);
                await client.CreateCollectionAsync(_opt.Collection,
                    new VectorParams { Size = _opt.VectorSize, Distance = Distance.Cosine },
                    cancellationToken: ct);
                // Índice no id do documento: deixa rápido filtrar/apagar por documento.
                await client.CreatePayloadIndexAsync(_opt.Collection, FieldDocumentId,
                    PayloadSchemaType.Keyword, cancellationToken: ct);
            }
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertAsync(IReadOnlyList<PointStruct> points, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        await client.UpsertAsync(_opt.Collection, points, cancellationToken: ct);
    }

    public async Task DeleteDocumentAsync(string documentId, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        await client.DeleteAsync(_opt.Collection, MatchKeyword(FieldDocumentId, documentId), cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ScoredPoint>> SearchAsync(
        ReadOnlyMemory<float> vector, int limit, float minScore, CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        return await client.QueryAsync(_opt.Collection,
            query: vector.ToArray(),
            scoreThreshold: minScore,
            limit: (ulong)limit,
            payloadSelector: true,
            cancellationToken: ct);
    }

    /// <summary>Lista os documentos indexados (percorre a collection inteira; ok para estudo).</summary>
    public async Task<IReadOnlyList<StoredDocument>> ListDocumentsAsync(CancellationToken ct)
    {
        await EnsureCollectionAsync(ct);
        var docs = new Dictionary<string, (string File, int Count)>();
        PointId? offset = null;

        do
        {
            var page = await client.ScrollAsync(_opt.Collection,
                limit: 256,
                offset: offset,
                payloadSelector: new WithPayloadSelector
                {
                    Include = new PayloadIncludeSelector { Fields = { FieldDocumentId, FieldFile } }
                },
                cancellationToken: ct);

            foreach (var point in page.Result)
            {
                var id = point.Payload[FieldDocumentId].StringValue;
                var file = point.Payload[FieldFile].StringValue;
                docs[id] = docs.TryGetValue(id, out var d) ? (d.File, d.Count + 1) : (file, 1);
            }

            offset = page.NextPageOffset;
        } while (offset is not null);

        return docs.Select(kv => new StoredDocument(kv.Key, kv.Value.File, kv.Value.Count))
                   .OrderBy(d => d.Arquivo)
                   .ToList();
    }
}
