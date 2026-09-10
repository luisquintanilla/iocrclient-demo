#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// PdfPigReader supports two honest modes in this demo:
//   Never: native PDF text through the shared document tree
//   AllPages: whole-document extraction through an injected IDocumentExtractionClient
//
// Per-page fallback is not exposed because the demo has no PDF rasterizer.

using DemoOcr;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.ML.Tokenizers;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

var nativeReader = new PdfPigReader(policy: OcrPolicy.Never);
IngestionDocument nativeDocument = await Read(nativeReader, pdf);
Console.WriteLine(
    $"[Never] native PdfPig -> {Pages(nativeDocument)} pages, {nativeDocument.Document.Nodes.Count} shared nodes, OCR calls: 0");

using IDocumentExtractionClient extractionClient = new FoundryMistralOcrClient(
    new Uri(DemoConfig.Require("OCR:FoundryEndpoint")),
    new Azure.Identity.DefaultAzureCredential());
var extractionReader = new PdfPigReader(extractionClient, OcrPolicy.AllPages);
IngestionDocument extractedDocument = await Read(extractionReader, pdf);
Console.WriteLine(
    $"[AllPages] IDocumentExtractionClient -> {Pages(extractedDocument)} pages, {extractedDocument.Document.Nodes.Count} shared nodes, OCR calls: {extractionReader.OcrCalls}");

var chunker = new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 0,
});
int chunks = 0;
await foreach (IngestionChunk _ in chunker.ProcessAsync(nativeDocument))
{
    chunks++;
}
Console.WriteLine($"Both supported modes feed the same chunker; native document chunks: {chunks}");
return 0;

static async Task<IngestionDocument> Read(IngestionDocumentReader reader, string path)
{
    await using FileStream source = File.OpenRead(path);
    return await reader.ReadAsync(source, Path.GetFileName(path), "application/pdf");
}

static int Pages(IngestionDocument document) =>
    document.Document.Nodes
        .SelectMany(node => node.PageReferences)
        .Select(reference => reference.PageNumber)
        .Distinct()
        .Count();
