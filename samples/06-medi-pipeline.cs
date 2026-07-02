#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9

// 06-medi-pipeline.cs — the document-AI engine as a MEDI reader, end to end. This is where the two
// abstractions meet:
//
//   IOcrClient (a capability)  --OcrDocumentReader-->  IngestionDocumentReader (a pipeline stage)
//                                                          -> SectionChunker -> IngestionChunk[]
//
// The line (the demo's core question): IOcrClient turns bytes into a normalized OcrResult and knows
// nothing about pipelines. IngestionDocumentReader is the MEDI front door and knows nothing about
// which engine. OcrDocumentReader is the ONE bridge — it composes ANY IOcrClient and never needs a
// per-engine subclass or a VisionOnly flag.
//
// It also grounds dotnet/extensions #7516 (opt-in chunk metadata) on the REAL chunker: name the
// element metadata keys you want and they survive onto chunks, so the pipeline can cite [page N].
//
//   az login  (keyless)
//   source .env.grounding            # OCR_FOUNDRY_ENDPOINT etc. — never committed
//   dotnet run 06-medi-pipeline.cs

using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

string endpoint = Require("OCR:FoundryEndpoint");
string model = DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

// --- 1. A real IOcrClient (#7588), wrapped in the real OCR middleware builder. Same shape as
//        ChatClientBuilder: you compose a client, you don't set flags. Swap the engine on one line. ---
using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
IOcrClient ocr = new FoundryMistralOcrClient(new Uri(endpoint), new DefaultAzureCredential(), model)
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

// --- 3. #7516, on the REAL chunker: opt in to exactly the provenance keys you need. ---
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

// BEFORE: default options name no keys -> provenance stays on elements, chunks lose it.
List<IngestionChunk> before = await ChunkAll(
    new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 512 }),
    document);

// AFTER: name the keys (#7516 MetadataKeysToPropagate) -> page_number survives onto every chunk.
var keys = new HashSet<string> { "page_number", "ocr_source", "confidence" };
List<IngestionChunk> after = await ChunkAll(
    new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 512, MetadataKeysToPropagate = keys }),
    document);

Console.WriteLine($"\n=== #7516 before / after (same document, same chunker, one option) ===");
Report("before (no keys named)", before);
Report("after  (page_number opted in)", after);

IngestionChunk? sample = after.FirstOrDefault(c => c.HasMetadata && c.Metadata.ContainsKey("page_number"));
if (sample is not null)
{
    Console.WriteLine($"\nchunk cites: page_number = {sample.Metadata["page_number"]}, ocr_source = {sample.Metadata["ocr_source"]}");
    Console.WriteLine("-> The provenance survives OCR -> reader -> chunk. The pipeline can cite [page N].");
}

static void Report(string label, List<IngestionChunk> chunks)
{
    bool any = chunks.Any(c => c.HasMetadata);
    Console.WriteLine($"  {label,-32}: {chunks.Count} chunk(s), carry metadata = {any}");
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

static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see .env.grounding); values are never committed.");
