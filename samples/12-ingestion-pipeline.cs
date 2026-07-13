#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9
#:package CommunityToolkit.VectorData.SqliteVec@1.0.0-preview.3

// 12-ingestion-pipeline.cs — the SAME OCR->RAG flow, but the REAL MEDI pipeline runs it end to end
// into a REAL local vector store. Samples 06/07 hand-composed the stages to teach them; this one
// hands them to IngestionPipeline and lets it drive:
//
//   IOcrClient (#7588) -> OcrDocumentReader -> IngestionPipeline{ SectionChunker -> VectorStoreWriter }
//                                                       |                                   |
//                                              emits OTEL activities              SqliteVec (local file)
//                                              + ILogger for free                 + real embeddings
//
// Two things become real here that were faked before:
//   1. The PIPELINE runs it (not hand-wired steps), so it emits OpenTelemetry + logs the hero app shows.
//   2. Retrieval is a real vector search over SqliteVec with real Azure OpenAI embeddings — not the
//      lexical keyword overlap the earlier samples used as a stand-in.
//
// Provenance note: the STOCK IngestionPipeline hides the chunks from the consumer, so there is no place
// to hand-roll per-chunk page tags here (that path is sample 07, which drives the stages itself). This
// sample's job is the pipeline + REAL vector retrieval end to end; page citation lives in sample 07.
//
//   dotnet user-secrets set "OCR:FoundryEndpoint"  "https://<account>.services.ai.azure.com" --id iocrclient-demo
//   dotnet user-secrets set "OCR:OpenAIEndpoint"   "https://<account>.openai.azure.com"      --id iocrclient-demo
//   dotnet user-secrets set "OCR:EmbedDeployment"  "text-embedding-3-small"                    --id iocrclient-demo
//   az login   # keyless (DefaultAzureCredential)
//   dotnet run 12-ingestion-pipeline.cs -- data/usgs-petroleum-assessment.pdf "What is the mean estimate of undiscovered technically recoverable oil in the province?"
//
// Vector store = CommunityToolkit.VectorData.SqliteVec (the community-home successor to the
// Semantic Kernel MEVD connectors), a local sqlite-vec file. No container needed.

using Azure.AI.OpenAI;
using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using CommunityToolkit.VectorData.SqliteVec;

const int EmbeddingDimensions = PageChunkRecord.Dim; // text-embedding-3-small

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string question = args.Length > 1 ? args[1] : "What is the mean estimate of undiscovered technically recoverable oil in the province?";
var cred = new DefaultAzureCredential();

using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddConsole());

// 1) OCR engine (#7588) behind the bridge — swap this one line to change engine.
using IOcrClient ocr = new FoundryMistralOcrClient(
    new Uri(DemoConfig.Require("OCR:FoundryEndpoint")),
    cred,
    DemoConfig.Get("OCR:MistralModel", "mistral-ocr-4-0"));
var reader = new OcrDocumentReader(ocr);

// 2) Real embeddings: Azure OpenAI -> IEmbeddingGenerator<string> -> adapted to AIContent input.
//    GetIngestionRecordCollection declares the vector property as VectorStoreVectorProperty<AIContent>,
//    so the store's generator must accept AIContent (the library's own writer/pipeline tests wire it as
//    IEmbeddingGenerator<AIContent, Embedding<float>>). AsTextContentEmbeddingGenerator yields a
//    <TextContent,...> generator, which does NOT satisfy the <AIContent> vector property — so we adapt
//    the string generator to AIContent input via the small AsAIContentEmbeddingGenerator helper below.
IEmbeddingGenerator<AIContent, Embedding<float>> embeddingGenerator =
    new AzureOpenAIClient(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), cred)
        .GetEmbeddingClient(DemoConfig.Get("OCR:EmbedDeployment", "text-embedding-3-small"))
        .AsIEmbeddingGenerator()
        .AsAIContentEmbeddingGenerator();

// 3) Real LOCAL vector store: SqliteVec (a plain file). Swap `new SqliteVectorStore(...)` for
//    `new InMemoryVectorStore(...)` for a zero-file run — MEVD keeps the rest identical.
string dbPath = Path.Combine(Path.GetTempPath(), "ocr-rag-demo.db");
using var vectorStore = new SqliteVectorStore(
    $"Data Source={dbPath};Pooling=false",
    new SqliteVectorStoreOptions { EmbeddingGenerator = embeddingGenerator });

VectorStoreCollection<Guid, PageChunkRecord> collection =
    vectorStore.GetIngestionRecordCollection<PageChunkRecord>("chunks", EmbeddingDimensions);

// 4) The pipeline. Reader -> chunker -> writer into the vector store.
//    IngestionPipeline drives it and emits OpenTelemetry + logs — nothing hand-wired.
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
using var writer = new VectorStoreWriter<PageChunkRecord>(collection);
using var pipeline = new IngestionPipeline(
    reader: reader,
    chunker: new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 256 }),
    writer: writer,
    loggerFactory: loggerFactory);

Console.WriteLine($"Ingesting {Path.GetFileName(pdf)} through the real IngestionPipeline -> SqliteVec ...");
await foreach (IngestionResult result in pipeline.ProcessAsync(new[] { new FileInfo(pdf) }))
{
    Console.WriteLine($"  ingested '{result.DocumentId}' — succeeded: {result.Succeeded}");
}

// 5) REAL vector retrieval (not lexical). The collection embeds the query with the same generator.
Console.WriteLine($"\nVector search: \"{question}\"");
var top = new List<PageChunkRecord>();
await foreach (VectorSearchResult<PageChunkRecord> hit in collection.SearchAsync(new TextContent(question), top: 4))
{
    top.Add(hit.Record);
    Console.WriteLine($"  score {hit.Score:F3}  {Preview(hit.Record)}");
}

// 6) Answer grounded ONLY on the retrieved chunks. (Per-chunk page citation is shown in sample 07;
//    the stock pipeline here hides the chunks, so there is no per-chunk provenance seam.)
string context = string.Join("\n\n", top.Select(Text));
IChatClient chat = new AzureOpenAIClient(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), cred)
    .GetChatClient(DemoConfig.Get("OCR:VisionDeployment", "gpt-4.1-mini"))
    .AsIChatClient();

ChatResponse answer = await chat.GetResponseAsync(
    "Answer the question using ONLY the context.\n\n" +
    $"Context:\n{context}\n\nQuestion: {question}");

Console.WriteLine($"\nQ: {question}\nA: {answer.Text}");
return 0;

static string Text(PageChunkRecord r) => (r.Content as TextContent)?.Text ?? r.Content?.ToString() ?? "";
static string Preview(PageChunkRecord r)
{
    string t = Text(r).Replace('\n', ' ').Trim();
    return t.Length <= 70 ? t : t[..70] + "…";
}

// The minimal vector record the MEDI pipeline writes: a known embedding dimension so the collection can
// be created up front. GetIngestionRecordCollection declares the vector property as
// VectorStoreVectorProperty<AIContent>, so Embedding is AIContent and the store's generator must accept
// AIContent input (see step 2). The stock VectorStoreWriter<PageChunkRecord> persists it — no override.
sealed class PageChunkRecord : IngestionChunkVectorRecord
{
    public const int Dim = 1536; // text-embedding-3-small

    [VectorStoreVector(Dim)]
    public override AIContent? Embedding => Content;
}

// Adapts an IEmbeddingGenerator<string,...> to accept AIContent input, extracting text from TextContent.
// Mirrors the library's AsTextContentEmbeddingGenerator, but targets AIContent so it matches the
// VectorStoreVectorProperty<AIContent> that GetIngestionRecordCollection declares (see step 2).
static class AIContentEmbeddingGeneratorExtensions
{
    public static IEmbeddingGenerator<AIContent, Embedding<float>> AsAIContentEmbeddingGenerator(
        this IEmbeddingGenerator<string, Embedding<float>> inner) => new Adapter(inner);

    sealed class Adapter : IEmbeddingGenerator<AIContent, Embedding<float>>
    {
        readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
        public Adapter(IEmbeddingGenerator<string, Embedding<float>> inner) => _inner = inner;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<AIContent> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
            => _inner.GenerateAsync(values.Select(v => (v as TextContent)?.Text ?? v.ToString() ?? string.Empty), options, ct);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }
}
