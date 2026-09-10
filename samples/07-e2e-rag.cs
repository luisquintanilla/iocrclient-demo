#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package CommunityToolkit.VectorData.InMemory@1.0.0-preview.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// Provider-backed end to end:
// IDocumentExtractionClient -> built-in DocumentExtractionReader -> current MEDI SectionChunker
// -> in-memory vector retrieval -> page-cited answer.

using Azure.AI.OpenAI;
using Azure.Identity;
using CommunityToolkit.VectorData.InMemory;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string question = args.Length > 1
    ? args[1]
    : "What is the mean estimate of undiscovered technically recoverable oil in the province?";
var credential = new DefaultAzureCredential();

using IDocumentExtractionClient extractionClient =
    new FoundryMistralOcrClient(new Uri(DemoConfig.Require("OCR:FoundryEndpoint")), credential);
var reader = new DocumentExtractionReader(extractionClient);

await using FileStream source = File.OpenRead(pdf);
IngestionDocument ingestion = await reader.ReadAsync(source, Path.GetFileName(pdf), "application/pdf");
Console.WriteLine($"Extraction -> built-in reader: {ingestion.Document.Nodes.Count} shared nodes");

var chunker = new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 256,
});
var chunks = new List<IngestionChunk>();
await foreach (IngestionChunk chunk in chunker.ProcessAsync(ingestion))
{
    chunks.Add(chunk);
}
Console.WriteLine(
    $"Chunked: {chunks.Count} chunks; pages {string.Join(", ", chunks.SelectMany(chunk => chunk.PageNumbers).Distinct().Order())}");

IEmbeddingGenerator<string, Embedding<float>> embedder =
    new AzureOpenAIClient(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), credential)
        .GetEmbeddingClient(DemoConfig.Get("OCR:EmbedDeployment", "text-embedding-3-small"))
        .AsIEmbeddingGenerator();

using var store = new InMemoryVectorStore(new() { EmbeddingGenerator = embedder });
VectorStoreCollection<Guid, RagChunk> collection = store.GetCollection<Guid, RagChunk>("chunks");
await collection.EnsureCollectionExistsAsync();
await collection.UpsertAsync(chunks.Select(chunk => new RagChunk
{
    Key = Guid.NewGuid(),
    Pages = string.Join(",", chunk.PageNumbers),
    Text = chunk.Content is TextContent text
        ? text.Text
        : chunk.Content.ToString() ?? string.Empty,
}));

var top = new List<RagChunk>();
await foreach (VectorSearchResult<RagChunk> result in collection.SearchAsync(question, top: 4))
{
    top.Add(result.Record);
}
Console.WriteLine($"Retrieved {top.Count} chunks from pages {string.Join(", ", top.Select(chunk => chunk.Pages).Distinct())}");

string context = string.Join("\n\n", top.Select(chunk => $"[page {chunk.Pages}] {chunk.Text}"));
IChatClient chat = new AzureOpenAIClient(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), credential)
    .GetChatClient(DemoConfig.Get("OCR:VisionDeployment", "gpt-4.1-mini"))
    .AsIChatClient();
ChatResponse answer = await chat.GetResponseAsync(
    "Answer the question using only the context. Cite each fact as [page N].\n\n" +
    $"Context:\n{context}\n\nQuestion: {question}");

Console.WriteLine($"Q: {question}");
Console.WriteLine($"A: {answer.Text}");
return 0;

sealed class RagChunk
{
    [VectorStoreKey]
    public Guid Key { get; set; }

    [VectorStoreData]
    public string Pages { get; set; } = string.Empty;

    [VectorStoreVector(1536)]
    public string Text { get; set; } = string.Empty;
}
