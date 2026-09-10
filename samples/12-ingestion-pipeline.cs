#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9
#:package CommunityToolkit.VectorData.SqliteVec@1.0.0-preview.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

using Azure.AI.OpenAI;
using Azure.Identity;
using CommunityToolkit.VectorData.SqliteVec;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

const int EmbeddingDimensions = 1536;
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string question = args.Length > 1
    ? args[1]
    : "What is the mean estimate of undiscovered technically recoverable oil in the province?";
var credential = new DefaultAzureCredential();
using ILoggerFactory loggerFactory = LoggerFactory.Create(
    builder => builder.SetMinimumLevel(LogLevel.Information).AddConsole());
using IDocumentExtractionClient extractionClient = new FoundryMistralOcrClient(
    new Uri(DemoConfig.Require("OCR:FoundryEndpoint")),
    credential,
    DemoConfig.Get("OCR:MistralModel", "mistral-ocr-4-0"));
var reader = new DocumentExtractionReader(extractionClient);

IEmbeddingGenerator<AIContent, Embedding<float>> embeddingGenerator =
    new AzureOpenAIClient(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), credential)
        .GetEmbeddingClient(DemoConfig.Get("OCR:EmbedDeployment", "text-embedding-3-small"))
        .AsIEmbeddingGenerator()
        .AsAIContentEmbeddingGenerator();

string dbPath = Path.Combine(Path.GetTempPath(), $"preview2-neutral-{Guid.NewGuid():N}.db");
try
{
    using var vectorStore = new SqliteVectorStore(
        $"Data Source={dbPath};Pooling=false",
        new() { EmbeddingGenerator = embeddingGenerator });
    VectorStoreCollection<Guid, PageChunkRecord> collection =
        vectorStore.GetIngestionRecordCollection<PageChunkRecord>("chunks", EmbeddingDimensions);
    using var writer = new VectorStoreWriter<PageChunkRecord>(collection);
    using var pipeline = new IngestionPipeline(
        reader,
        new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
        {
            MaxTokensPerChunk = 256,
            OverlapTokens = 0,
        }),
        writer,
        loggerFactory: loggerFactory);

    await foreach (IngestionResult result in pipeline.ProcessAsync([new FileInfo(pdf)]))
    {
        Console.WriteLine($"ingested '{result.DocumentId}' succeeded={result.Succeeded}");
    }

    var retrieved = new List<PageChunkRecord>();
    await foreach (VectorSearchResult<PageChunkRecord> hit in
        collection.SearchAsync(new TextContent(question), top: 4))
    {
        retrieved.Add(hit.Record);
        Console.WriteLine(
            $"score {hit.Score:F3} pages={string.Join(',', hit.Record.PageNumbers)} {Preview(hit.Record)}");
    }

    string context = string.Join(
        "\n\n",
        retrieved.Select(record =>
            $"[page {string.Join(',', record.PageNumbers)}] {Text(record)}"));
    IChatClient chat = new AzureOpenAIClient(
        new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")),
        credential)
        .GetChatClient(DemoConfig.Get("OCR:VisionDeployment", "gpt-4.1-mini"))
        .AsIChatClient();
    ChatResponse answer = await chat.GetResponseAsync(
        "Answer the question using only the context.\n\n" +
        $"Context:\n{context}\n\nQuestion: {question}");
    Console.WriteLine($"\nQ: {question}\nA: {answer.Text}");
    return 0;
}
finally
{
    DeleteIfPresent(dbPath);
    DeleteIfPresent(dbPath + "-shm");
    DeleteIfPresent(dbPath + "-wal");
}

static string Text(PageChunkRecord record) =>
    record.Content is TextContent text ? text.Text : string.Empty;

static string Preview(PageChunkRecord record)
{
    string text = Text(record).Replace('\n', ' ').Trim();
    return text.Length <= 70 ? text : text[..70] + "...";
}

static void DeleteIfPresent(string path)
{
    if (File.Exists(path))
    {
        File.Delete(path);
    }
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
