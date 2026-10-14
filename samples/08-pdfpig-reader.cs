#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#pragma warning disable EXTEXP0019, MEAI001, MEDE0001, MEAI002, MEAI003
using DemoOcr;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.ML.Tokenizers;

string pdf = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
    ?? "data/usgs-petroleum-assessment.pdf";
if (args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal) && argument != "--allow-live"))
{
    throw new ArgumentException("Use an optional PDF path and --allow-live.");
}
bool allowLive = args.Contains("--allow-live", StringComparer.Ordinal);

var native = new PdfPigReader(policy: OcrPolicy.Never);
using Document nativeDocument = await Read(native);
Report("Native PdfPig, no service calls", nativeDocument, native.OcrCalls);

if (allowLive)
{
    using IDocumentExtractionClient client = new FoundryMistralOcrClient(
        new Uri(DemoConfig.Require("OCR:FoundryEndpoint")), new Azure.Identity.DefaultAzureCredential());
    var hybrid = new PdfPigReader(client, OcrPolicy.FallbackForEmptyPages);
    using Document hybridDocument = await Read(hybrid);
    Report("Explicit empty-page recognition policy", hybridDocument, hybrid.OcrCalls);
    var all = new PdfPigReader(client, OcrPolicy.AllPages);
    using Document extracted = await Read(all);
    Report("Explicit whole-document provider extraction", extracted, all.OcrCalls);
}
else
{
    Console.WriteLine("Cloud policy comparisons not run. Use --allow-live and configure Mistral to enable them.");
}

var chunker = new SectionChunker(new IngestionChunkerOptions(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 256,
});
int chunks = 0;
await foreach (IngestionChunk chunk in chunker.ProcessAsync(nativeDocument))
{
    Console.WriteLine($"Chunk {++chunks}: source pages {string.Join(", ", chunk.GetSourcePages().Select(page => page.PageNumber))}");
}
Console.WriteLine($"The native shared Document produced {chunks} chunks without a second document model.");
return 0;

async Task<Document> Read(IngestionDocumentReader reader)
{
    await using FileStream source = File.OpenRead(pdf);
    return await reader.ReadAsync(source, Path.GetFileName(pdf), "application/pdf");
}
static void Report(string label, Document document, int calls)
    => Console.WriteLine($"{label}: {document.SourcePages.Count} known source pages, {document.Sections.Count} sections, {calls} provider calls.");
