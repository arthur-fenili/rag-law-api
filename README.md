# rag-law-api

Backend de um sistema RAG para estudo: perguntas sobre contratos, petições e concessões, com respostas citando arquivo e página.
Tudo roda **localmente e de graça**: nenhum documento sai da máquina.

Frontend: [`rag-law-frontend`](../rag-law-frontend) (React + Vite).

| Peça | Tecnologia |
|---|---|
| API | ASP.NET Core (.NET 10), Minimal API |
| LLM | Ollama + `qwen3:14b` (GPU) |
| Embeddings | Ollama + `bge-m3` (1024 dimensões, multilíngue) |
| Base vetorial | Qdrant (Docker) |
| Extração de PDF | PdfPig |
| Abstração de IA | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) |

## Como funciona

```
INGESTÃO   arquivo → texto por página (PdfPig) → chunks (~1500 chars, overlap 200)
           → embeddings (bge-m3) → Qdrant [vetor + arquivo, página, texto]

PERGUNTA   pergunta → embedding → Qdrant (top 5, similaridade ≥ 0.35)
           → prompt com os trechos numerados → qwen3 → resposta com citações [n]
```

## Pré-requisitos

- .NET 10 SDK
- Docker Desktop
- Ollama: `winget install Ollama.Ollama`

## Rodando

Uma vez só:

```bash
ollama pull bge-m3
ollama pull qwen3:14b
```

Cada vez que for usar:

```bash
ollama serve                          # se o Ollama não estiver aberto
docker compose up -d                  # Qdrant
dotnet run --project RagJuridico.Api --launch-profile http
```

| Serviço | Endereço |
|---|---|
| API | http://localhost:5024 |
| Qdrant (dashboard, ver os chunks) | http://localhost:6333/dashboard |
| Ollama | http://localhost:11434 |

Para liberar a GPU ao terminar:

```bash
docker compose down
```

Depois feche o Ollama pelo ícone da bandeja (Quit) ou com `taskkill /IM ollama.exe /F`.

## Endpoints

| Método | Rota | O que faz |
|---|---|---|
| GET | `/health` | saúde da API |
| POST | `/api/documentos` | envia PDF/TXT/MD (campo `arquivo`) e indexa |
| POST | `/api/documentos/preview` | mostra como o arquivo seria dividido, sem indexar |
| GET | `/api/documentos` | lista os documentos indexados |
| DELETE | `/api/documentos/{id}` | remove um documento |
| POST | `/api/chat` | `{ "pergunta": "..." }` → resposta + fontes |

Requisições prontas: [`RagJuridico.Api/RagJuridico.Api.http`](RagJuridico.Api/RagJuridico.Api.http).
Contrato fictício para testes: [`samples/contrato-exemplo.pdf`](samples/contrato-exemplo.pdf).

CORS: liberado para `http://localhost:5173` (chave `Cors:Origins` no `appsettings.json`).

## Configuração (`RagJuridico.Api/appsettings.json`)

| Chave | Padrão | Observação |
|---|---|---|
| `Ollama:ChatModel` | `qwen3:14b` | teste também `qwen3:8b`, `gemma3:12b` |
| `Ollama:EmbeddingModel` | `bge-m3` | trocar exige ajustar `Qdrant:VectorSize` e reindexar |
| `Qdrant:VectorSize` | `1024` | dimensão do modelo de embeddings |
| `Rag:ChunkSize` / `ChunkOverlap` | `1500` / `200` | em caracteres |
| `Rag:TopK` | `5` | trechos enviados ao LLM |
| `Rag:MinScore` | `0.35` | similaridade mínima |
| `Rag:DisableThinking` | `false` | `true` = Qwen3 responde mais rápido, sem raciocinar |

## Estrutura

```
RagJuridico.Api/
  Program.cs                      DI + endpoints + CORS
  Options/Options.cs              configurações tipadas
  Services/DocumentTextExtractor  PDF/TXT → texto por página
  Services/TextChunker            parágrafos → frases → corte, com overlap
  Services/VectorStore            Qdrant (collection, upsert, busca, listagem)
  Services/IngestionService       pipeline de ingestão
  Services/RagChatService         recuperação + prompt + geração
samples/                          contrato fictício para testes
docker-compose.yml                Qdrant
```

## Próximos passos (roadmap)

1. Chunking por cláusula/artigo (regex em "CLÁUSULA", "Art.") em vez de tamanho fixo
2. Aumentar contexto do Ollama (padrão 4096 tokens)
3. Streaming da resposta (SSE)
4. Histórico de conversa (hoje cada pergunta é independente)
5. Agente com tools (`buscar_documentos`, `comparar_clausulas`, `calcular_prazo`)
6. Busca híbrida (BM25 + vetor) e reranking
7. Conjunto de avaliação: perguntas com resposta esperada, medido a cada mudança
8. OCR para PDFs escaneados

> ⚠️ O LLM pode errar. Toda resposta deve ser conferida nas fontes citadas e revisada por um advogado.
