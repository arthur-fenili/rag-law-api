using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using RagJuridico.Api.Options;

namespace RagJuridico.Api.Services;

public sealed record Source(int Numero, string Arquivo, int Pagina, double Similaridade, string Trecho);

public sealed record ChatAnswer(string Resposta, IReadOnlyList<Source> Fontes, string Modelo, long DuracaoMs);

/// <summary>
/// Pergunta → embedding da pergunta → busca no Qdrant → prompt com os trechos → LLM → resposta com citações.
/// </summary>
public sealed partial class RagChatService(
    IChatClient chat,
    IEmbeddingGenerator<string, Embedding<float>> embedder,
    VectorStore store,
    IOptions<RagOptions> ragOptions,
    IOptions<OllamaOptions> ollamaOptions)
{
    private const string SystemPrompt = """
        Você é um assistente jurídico que ajuda uma estagiária de Direito a analisar contratos, petições e concessões.

        Regras:
        - Responda sempre em português do Brasil, de forma clara e objetiva.
        - Use SOMENTE as informações dos trechos dentro de <fontes>. Não use conhecimento externo para afirmar fatos sobre os documentos.
        - Cite a fonte de cada afirmação no formato [n], logo após a frase, onde n é o número do trecho.
        - Se os trechos não trouxerem a resposta, diga claramente que não encontrou essa informação nos documentos. Nunca invente cláusulas, valores, datas ou partes.
        - Quando houver ambiguidade ou risco jurídico, aponte isso e recomende revisão por um advogado.
        """;

    private readonly RagOptions _rag = ragOptions.Value;
    private readonly string _model = ollamaOptions.Value.ChatModel;

    public async Task<ChatAnswer> AskAsync(string question, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 1. Recuperação
        var queryVector = await embedder.GenerateVectorAsync(question, cancellationToken: ct);
        var hits = await store.SearchAsync(queryVector, _rag.TopK, _rag.MinScore, ct);

        var sources = hits.Select((h, i) => new Source(
            Numero: i + 1,
            Arquivo: h.Payload[VectorStore.FieldFile].StringValue,
            Pagina: (int)h.Payload[VectorStore.FieldPage].IntegerValue,
            Similaridade: Math.Round(h.Score, 3),
            Trecho: h.Payload[VectorStore.FieldText].StringValue)).ToList();

        if (sources.Count == 0)
            return new ChatAnswer(
                "Não encontrei trechos relevantes nos documentos indexados para responder a essa pergunta.",
                sources, _model, sw.ElapsedMilliseconds);

        // 2. Montagem do prompt
        var prompt = new StringBuilder("<fontes>\n");
        foreach (var s in sources)
            prompt.Append($"[{s.Numero}] {s.Arquivo}, página {s.Pagina}\n{s.Trecho}\n\n");
        prompt.Append("</fontes>\n\n");
        prompt.Append($"Pergunta: {question}");
        if (_rag.DisableThinking) prompt.Append(" /no_think");

        // 3. Geração
        List<ChatMessage> messages =
        [
            new(ChatRole.System, SystemPrompt),
            new(ChatRole.User, prompt.ToString()),
        ];
        var response = await chat.GetResponseAsync(messages, new ChatOptions { Temperature = _rag.Temperature }, ct);

        return new ChatAnswer(StripThinking(response.Text), sources, _model, sw.ElapsedMilliseconds);
    }

    /// <summary>Modelos de raciocínio (Qwen3) podem devolver o "pensamento" entre &lt;think&gt; tags.</summary>
    private static string StripThinking(string text) => ThinkTags().Replace(text, "").Trim();

    [GeneratedRegex(@"<think>[\s\S]*?</think>")]
    private static partial Regex ThinkTags();
}
