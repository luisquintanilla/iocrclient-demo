#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9
#:package CommunityToolkit.VectorData.SqliteVec@1.0.0-preview.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
using Microsoft.Extensions.DocumentExtraction;

// 12-ingestion-pipeline.cs — the SAME OCR->RAG flow, but the REAL MEDI pipeline runs it end to end
// into a REAL local vector store. Samples 06/07 hand-composed the stages to teach them; this one
// hands them to IngestionPipeline and lets it drive:
//
//   IDocumentExtractionClient -> DocumentExtractionReader -> IngestionPipeline
//                                                          -> SectionChunker
//                                                          -> VectorStoreWriter<PageChunkRecord>
//
// Two things become real here that were faked before:
//   1. The PIPELINE runs it (not hand-wired steps), so it emits OpenTelemetry + logs the hero app shows.
//   2. Retrieval is a real vector search over SqliteVec with real Azure OpenAI embeddings — not the
//      lexical keyword overlap the earlier samples used as a stand-in.
//
// Preview 2 chunks are non-generic AIContent values with required TokenCount. The typed stock writer
// persists polymorphic content and page numbers, then the configured vector provider embeds on upsert.
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

const int EmbeddingDimensions = 1536; // text-embedding-3-small

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string question = args.Length > 1 ? args[1] : "What is the mean estimate of undiscovered technically recoverable oil in the province?";
var cred = new DefaultAzureCredential();

using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddConsole());

// 1) OCR engine behind the built-in bridge. Swap this one line to change engine.
using IDocumentExtractionClient ocr = new FoundryMistralOcrClient(
    new Uri(DemoConfig.Require("OCR:FoundryEndpoint")),
    cred,
    DemoConfig.Get("OCR:MistralModel", "mistral-ocr-4-0"));
var reader = new DocumentExtractionReader(
    ocr,
    new() { MarkdownOnlyPagePolicy = MarkdownOnlyPagePolicy.PreserveAsMarkdown });

// 2) Real embeddings: Azure OpenAI, adapted at the provider boundary from AIContent to text.
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

// 4) The pipeline. Reader -> chunker -> writer into the vector store.
//    IngestionPipeline drives it and emits OpenTelemetry + logs — nothing hand-wired.
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
VectorStoreCollection<Guid, PageChunkRecord> collection =
    vectorStore.GetIngestionRecordCollection<PageChunkRecord>("chunks", EmbeddingDimensions);
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
await foreach (VectorSearchResult<PageChunkRecord> hit in
    writer.VectorStoreCollection.SearchAsync(new TextContent(question), top: 4))
{
    top.Add(hit.Record);
    Console.WriteLine($"  score {hit.Score:F3}  pages {string.Join(",", hit.Record.PageNumbers)}: {Preview(hit.Record)}");
}

// 6) Answer grounded only on the retrieved chunks and their persisted page numbers.
string context = string.Join(
    "\n\n",
    top.Select(record => $"[page {string.Join(",", record.PageNumbers)}] {Text(record)}"));
IChatClient chat = new AzureOpenAIClient(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), cred)
    .GetChatClient(DemoConfig.Get("OCR:VisionDeployment", "gpt-4.1-mini"))
    .AsIChatClient();

ChatResponse answer = await chat.GetResponseAsync(
    "Answer the question using ONLY the context.\n\n" +
    $"Context:\n{context}\n\nQuestion: {question}");

Console.WriteLine($"\nQ: {question}\nA: {answer.Text}");
return 0;

static string Text(PageChunkRecord record) => (record.Content as TextContent)?.Text ?? "";
static string Preview(PageChunkRecord record)
{
    string t = Text(record).Replace('\n', ' ').Trim();
    return t.Length <= 70 ? t : t[..70] + "…";
}

sealed class PageChunkRecord : IngestionChunkVectorRecord
{
    [VectorStoreVector(1536)]
    public override AIContent? Embedding => Content;
}

static class AIContentEmbeddingGeneratorExtensions
{
    public static IEmbeddingGenerator<AIContent, Embedding<float>> AsAIContentEmbeddingGenerator(
        this IEmbeddingGenerator<string, Embedding<float>> inner) => new Adapter(inner);

    private sealed class Adapter(IEmbeddingGenerator<string, Embedding<float>> inner)
        : IEmbeddingGenerator<AIContent, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<AIContent> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => inner.GenerateAsync(
                values.Select(value => value is TextContent text
                    ? text.Text
                    : throw new NotSupportedException(
                        $"Sample 12 only embeds text; received {value.GetType().Name}.")),
                options,
                cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }
}
