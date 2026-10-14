#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#pragma warning disable EXTEXP0019, MEAI001, MEDE0001, MEAI002, MEAI003

using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.ML.Tokenizers;

// Both readers return the shared Document. No second tree or section reparenting.
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
bool live = args.Contains("--allow-live", StringComparer.Ordinal);
using IDocumentExtractionClient? client = live
    ? new FoundryMistralOcrClient(new Uri(DemoConfig.Require("OCR:FoundryEndpoint")), new DefaultAzureCredential())
    : null;
IngestionDocumentReader reader = client is null
    ? new PdfPigReader(policy: OcrPolicy.Never)
    : new DocumentExtractionReader(client);

await using FileStream source = File.OpenRead(pdf);
using Document document = await reader.ReadAsync(source, Path.GetFileName(pdf), "application/pdf");
Console.WriteLine($"Shared Document: {document.Sections.Count} sections, {document.SourcePages.Count} known source pages");
Console.WriteLine(live ? "Extraction: live Mistral" : "Extraction: native PdfPig, no cloud calls");

var chunker = new SectionChunker(new IngestionChunkerOptions(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 512,
});
int count = 0;
await foreach (IngestionChunk chunk in chunker.ProcessAsync(document))
{
    int[] pages = chunk.GetSourcePages().Select(page => page.PageNumber).Distinct().Order().ToArray();
    Console.WriteLine($"Chunk {++count}: {chunk.SourceElements.Count} contributing elements; source pages: {(pages.Length == 0 ? "unknown" : string.Join(", ", pages))}");
}
if (document.IsDisposed)
{
    throw new InvalidOperationException("The chunker disposed its borrowed Document.");
}
Console.WriteLine($"Chunked {count} records without cloning or reparenting shared sections.");
