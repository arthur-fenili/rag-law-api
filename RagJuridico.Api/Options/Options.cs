namespace RagJuridico.Api.Options;

/// <summary>Configuração do Ollama (LLM + embeddings locais).</summary>
public sealed class OllamaOptions
{
    public string Url { get; set; } = "http://localhost:11434";
    public string ChatModel { get; set; } = "qwen3:14b";
    public string EmbeddingModel { get; set; } = "bge-m3";
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>Configuração do Qdrant (base vetorial).</summary>
public sealed class QdrantOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 6334; // gRPC
    public string Collection { get; set; } = "documentos";

    /// <summary>Precisa bater com a dimensão do modelo de embeddings (bge-m3 = 1024).</summary>
    public ulong VectorSize { get; set; } = 1024;
}

/// <summary>Parâmetros do pipeline RAG.</summary>
public sealed class RagOptions
{
    /// <summary>Tamanho máximo de cada chunk, em caracteres (~4 caracteres por token).</summary>
    public int ChunkSize { get; set; } = 1500;

    /// <summary>Sobreposição entre chunks consecutivos, em caracteres.</summary>
    public int ChunkOverlap { get; set; } = 200;

    /// <summary>Quantos trechos recuperar por pergunta.</summary>
    public int TopK { get; set; } = 5;

    /// <summary>Similaridade mínima (cosseno) para um trecho ser considerado relevante.</summary>
    public float MinScore { get; set; } = 0.35f;

    public float Temperature { get; set; } = 0.2f;

    /// <summary>Qwen3: adiciona "/no_think" na pergunta para responder mais rápido, sem raciocínio.</summary>
    public bool DisableThinking { get; set; } = false;
}
