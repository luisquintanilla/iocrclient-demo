#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// 06-medi-pipeline.cs: a real provider through the built-in explicit bridge.
//
//   IDocumentExtractionClient -> DocumentExtractionReader -> SectionChunker -> IngestionChunk
//
// Mistral returns provider Markdown. PreserveAsMarkdown is the explicit bridge policy for pages
// without canonical elements. MEDI now carries typed page numbers on each chunk.
//
//   az login  (keyless)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet run 06-medi-pipeline.cs

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

// --- 1. A real IDocumentExtractionClient. Swap the engine on one line. ---
using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddConsole());
using IDocumentExtractionClient ocr =
    new FoundryMistralOcrClient(new Uri(endpoint), new DefaultAzureCredential(), model);

// --- 2. The built-in explicit bridge from the comparison PR. ---
var reader = new DocumentExtractionReader(
    ocr,
    new()
    {
        MarkdownOnlyPagePolicy = MarkdownOnlyPagePolicy.PreserveAsMarkdown,
    });

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

// --- 3. Preview 2's non-generic chunker emits AIContent + required TokenCount + typed pages. ---
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
var chunker = new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 512 });

var chunks = new List<IngestionChunk>();
await foreach (IngestionChunk chunk in chunker.ProcessAsync(document))
{
    chunks.Add(chunk);
}

Console.WriteLine($"\n=== typed provenance on chunks ===");
Console.WriteLine($"  chunks: {chunks.Count}");
Console.WriteLine($"  pages : {string.Join(" | ", chunks.Select(chunk => string.Join(",", chunk.PageNumbers)))}");
if (chunks.Count > 0)
{
    Console.WriteLine($"\nfirst chunk cites page(s): {string.Join(", ", chunks[0].PageNumbers)}");
    Console.WriteLine("-> The built-in bridge and chunker preserve page provenance.");
}

static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see .env.grounding); values are never committed.");
