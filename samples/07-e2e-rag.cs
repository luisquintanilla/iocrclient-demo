#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package CommunityToolkit.VectorData.InMemory@1.0.0-preview.3

// 07-e2e-rag.cs — the whole point, end to end, on the REAL MEDI pipeline:
//
//   IOcrClient (#7588)  ->  OcrDocumentReader  ->  SectionChunker (per page)  ->  vector retrieve  ->  cited answer
//
// Both abstractions pay off together. IOcrClient turns the PDF into page-structured Markdown; the
// OcrDocumentReader bridges it into MEDI (one section per OCR page, each stamped with its page). The
// SectionChunker is section-bounded, so chunking each page-section on its own tags every chunk with its
// exact source page — the answer cites [page N] with zero cross-page bleed and no framework opt-in.
// Swap the OCR provider on one line and nothing else changes. Retrieval is REAL here: a real
// IEmbeddingGenerator (Azure OpenAI embeddings) + a local Microsoft.Extensions.VectorData store
// (CommunityToolkit.VectorData.InMemory). The store is interchangeable — swap InMemoryVectorStore for
// SqliteVectorStore (see 12) and nothing else changes. Sample 12 runs the SAME shape through the MEDI
// IngestionPipeline + VectorStoreWriter.
//
//   az login  (keyless)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet run 07-e2e-rag.cs -- data/usgs-petroleum-assessment.pdf "What is the mean estimate of undiscovered technically recoverable oil in the province?"

using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Identity;
using CommunityToolkit.VectorData.InMemory;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string question = args.Length > 1 ? args[1] : "What is the mean estimate of undiscovered technically recoverable oil in the province?";
var cred = new DefaultAzureCredential();

// 1) OCR — swap this one line to change engine. Everything below is provider-agnostic.
using IOcrClient ocr = new FoundryMistralOcrClient(new Uri(Require("OCR:FoundryEndpoint")), cred);
var reader = new OcrDocumentReader(ocr);

IngestionDocument document;
await using (FileStream src = File.OpenRead(pdf))
{
    document = await reader.ReadAsync(src, Path.GetFileName(pdf), "application/pdf");
}
Console.WriteLine($"OCR -> reader: {document.Sections.Count} pages of structured elements");

// 2) Chunk with the real MEDI SectionChunker — one page at a time so every chunk keeps its source
//    page. SectionChunker is section-bounded and the bridge emits one section per OCR page, so this
//    yields the same chunks as whole-document chunking but with exact, always-correct page provenance.
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
var chunker = new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 256 });

var chunks = new List<(string Text, int Page)>();
await foreach ((string Text, int Page) c in ChunkByPage(document, chunker))
{
    chunks.Add(c);
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
await collection.UpsertAsync(chunks.Select(c => new RagChunk { Key = Guid.NewGuid(), Page = c.Page, Text = c.Text }));

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

static string ChunkText(IngestionChunk c) => (c.Content as TextContent)?.Text ?? c.Content?.ToString() ?? string.Empty;

// Hand-rolled page provenance. SectionChunker is section-bounded and OcrDocumentReader emits one
// section per OCR page (stamped section.Metadata["page_number"]), so chunking each page-section on its
// own tags every emitted chunk with its exact source page — no cross-page bleed, no framework opt-in.
static async IAsyncEnumerable<(string Text, int Page)> ChunkByPage(
    IngestionDocument document, SectionChunker chunker,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    foreach (IngestionDocumentSection section in document.Sections)
    {
        int page = section.Metadata.TryGetValue("page_number", out object? p) && p is int i ? i : -1;
        var pageDoc = new IngestionDocument(document.Identifier);
        pageDoc.Sections.Add(section);
        await foreach (IngestionChunk c in chunker.ProcessAsync(pageDoc, ct))
        {
            yield return (ChunkText(c), page);
        }
    }
}

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
