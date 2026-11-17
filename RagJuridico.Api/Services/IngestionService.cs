using System.Security.Cryptography;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Qdrant.Client.Grpc;
using RagJuridico.Api.Options;

namespace RagJuridico.Api.Services;

public sealed record IngestResult(string DocumentoId, string Arquivo, int Paginas, int Trechos);

/// <summary>
/// Ingestão: arquivo → texto por página → chunks → embeddings (bge-m3) → Qdrant.
/// O id do documento é o hash do conteúdo: reenviar o mesmo arquivo substitui os trechos antigos.
/// </summary>
public sealed class IngestionService(
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    VectorStore store,
    IOptions<RagOptions> ragOptions,
    IOptions<QdrantOptions> qdrantOptions,
    ILogger<IngestionService> logger)
{
    private const int EmbeddingBatchSize = 32;
    private readonly RagOptions _rag = ragOptions.Value;
    private readonly ulong _vectorSize = qdrantOptions.Value.VectorSize;

    public async Task<IngestResult> IngestAsync(Stream content, string fileName, CancellationToken ct)
    {
        // PdfPig precisa de stream "seekable"; copiamos para memória.
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        buffer.Position = 0;
        var documentId = Convert.ToHexString(await SHA256.HashDataAsync(buffer, ct))[..16].ToLowerInvariant();

        buffer.Position = 0;
        var pages = DocumentTextExtractor.Extract(buffer, fileName);
        var chunks = TextChunker.Split(pages, _rag.ChunkSize, _rag.ChunkOverlap).ToList();

        if (chunks.Count == 0)
            throw new InvalidOperationException(
                "Nenhum texto extraído. Se for um PDF escaneado (imagem), ele precisa de OCR antes.");

        logger.LogInformation("{File}: {Pages} páginas, {Chunks} trechos", fileName, pages.Count, chunks.Count);

        var points = new List<PointStruct>(chunks.Count);
        foreach (var batch in chunks.Chunk(EmbeddingBatchSize))
        {
            var embeddings = await embedder.GenerateAsync(batch.Select(c => c.Text), cancellationToken: ct);

            for (var i = 0; i < batch.Length; i++)
            {
                var vector = embeddings[i].Vector;
                if ((ulong)vector.Length != _vectorSize)
                    throw new InvalidOperationException(
                        $"O modelo de embeddings gerou {vector.Length} dimensões, mas a collection espera {_vectorSize}. " +
                        "Ajuste Qdrant:VectorSize e recrie a collection.");

                points.Add(new PointStruct
                {
                    Id = Guid.NewGuid(),
                    Vectors = vector.ToArray(),
                    Payload =
                    {
                        [VectorStore.FieldDocumentId] = documentId,
                        [VectorStore.FieldFile] = fileName,
                        [VectorStore.FieldPage] = batch[i].Page,
                        [VectorStore.FieldText] = batch[i].Text,
                    }
                });
            }
        }

        await store.DeleteDocumentAsync(documentId, ct);
        await store.UpsertAsync(points, ct);

        return new IngestResult(documentId, fileName, pages.Count, chunks.Count);
    }
}
