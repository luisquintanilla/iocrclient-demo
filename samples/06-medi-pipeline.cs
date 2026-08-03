#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// 06-medi-pipeline.cs — the document-AI engine as a MEDI reader, end to end. This is where the two
// abstractions meet:
//
//   IDocumentExtractionClient (a capability)  --OcrDocumentReader-->  IngestionDocumentReader (a pipeline stage)
//                                                          -> SectionChunker -> IngestionChunk[]
//
// The line (the demo's core question): IDocumentExtractionClient turns bytes into a normalized DocumentExtractionResult and knows
// nothing about pipelines. IngestionDocumentReader is the MEDI front door and knows nothing about
// which engine. OcrDocumentReader is the ONE bridge — it composes ANY IDocumentExtractionClient and never needs a
// per-engine subclass or a VisionOnly flag.
//
// It also shows how to keep page provenance end to end on the REAL chunker: the stock SectionChunker
// does not copy element metadata onto chunks, so chunk within page boundaries and every chunk keeps
// its source page — the pipeline can then cite [page N] with no framework opt-in.
//
//   az login  (keyless)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet run 06-medi-pipeline.cs

using System.Runtime.CompilerServices;
using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

string endpoint = Require("OCR:FoundryEndpoint");
string model = DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

// --- 1. A real IDocumentExtractionClient (#7588), wrapped in the real OCR middleware builder. Same shape as
//        ChatClientBuilder: you compose a client, you don't set flags. Swap the engine on one line. ---
using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
IDocumentExtractionClient ocr = new FoundryMistralOcrClient(new Uri(endpoint), new DefaultAzureCredential(), model)
    .AsBuilder()
    .UseLogging(loggerFactory)
    .Build();

// --- 2. The bridge. One reader composes the engine; the engine is the constructor argument. ---
var reader = new OcrDocumentReader(ocr);

IngestionDocument document;
await using (FileStream src = File.OpenRead(pdf))
{
    document = await reader.ReadAsync(src, identifier: Path.GetFileName(pdf), mediaType: "application/pdf");
}

int elementCount = document.EnumerateContent().Count();
Console.WriteLine($"=== IngestionDocument: {document.Sections.Count} section(s), {elementCount} element(s) ===");
foreach (IngestionDocumentElement el in document.EnumerateContent().Take(3))
{
    Console.WriteLine($"\n[page {el.PageNumber}] {el.GetType().Name}: {Trim(el.GetMarkdown(), 80)}");
    if (el.HasMetadata)
    {
        Console.WriteLine($"  element metadata: {string.Join(", ", el.Metadata.Keys)}");
    }
}

// --- 3. Carry provenance onto chunks. The stock SectionChunker does NOT copy element metadata onto
//        chunks, so page provenance is lost by default. Recover it by chunking each page-section on its
//        own: SectionChunker is section-bounded and the reader emits one section per page, so every
//        chunk keeps its exact source page — no cross-page bleed, no framework opt-in. ---
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
var chunker = new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 512 });

// BEFORE: chunk the whole document -> chunks are page-blind (element metadata does not ride along).
List<IngestionChunk> whole = await ChunkAll(chunker, document);

// AFTER: chunk per page -> every chunk is tagged with its source page.
var tagged = new List<(IngestionChunk Chunk, int Page)>();
await foreach ((IngestionChunk Chunk, int Page) c in ChunkByPage(document, chunker))
{
    tagged.Add(c);
}

Console.WriteLine($"\n=== provenance onto chunks (same document, same chunker) ===");
Console.WriteLine($"  whole-document chunk : {whole.Count,3} chunk(s), any carry page metadata = {whole.Any(c => c.HasMetadata)}");
Console.WriteLine($"  per-page chunk       : {tagged.Count,3} chunk(s), every chunk knows its page = {tagged.All(t => t.Page >= 0)}");

object? ocrSource = document.EnumerateContent()
    .FirstOrDefault(e => e.HasMetadata && e.Metadata.ContainsKey("ocr_source"))?.Metadata["ocr_source"];
var cite = tagged.FirstOrDefault(t => t.Page >= 0);
if (cite.Chunk is not null)
{
    Console.WriteLine($"\nchunk cites: page = {cite.Page}, ocr_source = {ocrSource}");
    Console.WriteLine("-> The provenance survives OCR -> reader -> chunk. The pipeline can cite [page N].");
}

static async Task<List<IngestionChunk>> ChunkAll(IngestionChunker chunker, IngestionDocument doc)
{
    var list = new List<IngestionChunk>();
    await foreach (IngestionChunk c in chunker.ProcessAsync(doc, default))
    {
        list.Add(c);
    }
    return list;
}

// Hand-rolled page provenance: chunk each page-section on its own so every chunk keeps its source page.
static async IAsyncEnumerable<(IngestionChunk Chunk, int Page)> ChunkByPage(
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
            yield return (c, page);
        }
    }
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see .env.grounding); values are never committed.");
