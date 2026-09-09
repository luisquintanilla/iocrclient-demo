#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package CommunityToolkit.VectorData.InMemory@1.0.0-preview.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// 07-e2e-rag.cs — the whole point, end to end, on the REAL MEDI pipeline:
//
//   IDocumentExtractionClient -> DocumentExtractionReader -> SectionChunker -> vector retrieve -> cited answer
//
// Both abstractions pay off together. IDocumentExtractionClient turns the PDF into page-structured Markdown; the
// The built-in bridge maps extraction pages into MEDI sections. SectionChunker carries typed source
// page numbers on every chunk, so the answer can cite [page N].
// Swap the OCR provider on one line and nothing else changes. Retrieval is REAL here: a real
// IEmbeddingGenerator (Azure OpenAI embeddings) + a local Microsoft.Extensions.VectorData store
// (CommunityToolkit.VectorData.InMemory). The store is interchangeable — swap InMemoryVectorStore for
// SqliteVectorStore (see 12) and nothing else changes. Sample 12 runs the SAME shape through the MEDI
// IngestionPipeline + VectorStoreWriter.
//
//   az login  (keyless)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet run 07-e2e-rag.cs -- data/usgs-petroleum-assessment.pdf "What is the mean estimate of undiscovered technically recoverable oil in the province?"

using Azure.AI.OpenAI;
using Azure.Identity;
using CommunityToolkit.VectorData.InMemory;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string question = args.Length > 1 ? args[1] : "What is the mean estimate of undiscovered technically recoverable oil in the province?";
var cred = new DefaultAzureCredential();

// 1) OCR — swap this one line to change engine. Everything below is provider-agnostic.
using IDocumentExtractionClient ocr = new FoundryMistralOcrClient(new Uri(Require("OCR:FoundryEndpoint")), cred);
var reader = new DocumentExtractionReader(
    ocr,
    new() { MarkdownOnlyPagePolicy = MarkdownOnlyPagePolicy.PreserveAsMarkdown });

IngestionDocument document;
await using (FileStream src = File.OpenRead(pdf))
{
    document = await reader.ReadAsync(src, Path.GetFileName(pdf), "application/pdf");
}
Console.WriteLine($"OCR -> reader: {document.Sections.Count} pages of structured elements");

// 2) Chunk with the real Preview 2 SectionChunker. Content is AIContent and TokenCount is required.
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
var chunker = new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 256 });

var chunks = new List<IngestionChunk>();
await foreach (IngestionChunk chunk in chunker.ProcessAsync(document))
{
    chunks.Add(chunk);
}
Console.WriteLine($"Chunked: {chunks.Count} chunks, each tagged with its source page");

// 3) Retrieve top-k with a REAL embedding + local vector store (interchangeable via MEVD).
IEmbeddingGenerator<string, Embedding<float>> embedder =
    new AzureOpenAIClient(new Uri(Require("OCR:OpenAIEndpoint")), cred)
        .GetEmbeddingClient(DemoOcr.DemoConfig.Config["OCR:EmbedDeployment"] ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator();

using var store = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = embedder });
var collection = store.GetCollection<Guid, RagChunk>("chunks");
await collection.EnsureCollectionExistsAsync();
await collection.UpsertAsync(chunks.Select(c => new RagChunk
{
    Key = Guid.NewGuid(),
    Page = c.PageNumbers.Single(),
    Text = (c.Content as TextContent)?.Text ?? c.Content.ToString() ?? "",
}));

var top = new List<(string Text, int Page)>();
await foreach (VectorSearchResult<RagChunk> r in collection.SearchAsync(question, top: 4))
    top.Add((r.Record.Text, r.Record.Page));
Console.WriteLine($"Retrieved {top.Count} chunks (vector similarity): pages {string.Join(", ", top.Select(x => x.Page).Where(p => p >= 0).Distinct())}\n");

// 4) Answer, grounded ONLY on the retrieved chunks, each citable to its real page number.
string context = string.Join("\n\n", top.Select(x => $"[page {x.Page}] {x.Text}"));
IChatClient chat = new AzureOpenAIClient(new Uri(Require("OCR:OpenAIEndpoint")), cred)
    .GetChatClient(DemoOcr.DemoConfig.Config["OCR:VisionDeployment"] ?? "gpt-4.1-mini")
    .AsIChatClient();

var prompt =
    "Answer the question using ONLY the context. Cite the page for each fact like [page N].\n\n" +
    $"Context:\n{context}\n\nQuestion: {question}";
ChatResponse answer = await chat.GetResponseAsync(prompt);

Console.WriteLine($"Q: {question}");
Console.WriteLine($"A: {answer.Text}");
return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} via user-secrets (--id iocrclient-demo); values are never committed.");

// Local vector-store record. The [VectorStoreVector] property holds the source text; the store's
// configured IEmbeddingGenerator embeds it at upsert/search time. Swap InMemory -> SqliteVec unchanged.
sealed class RagChunk
{
    [VectorStoreKey] public Guid Key { get; set; }
    [VectorStoreData] public int Page { get; set; }
    [VectorStoreVector(1536)] public string Text { get; set; } = "";
}
