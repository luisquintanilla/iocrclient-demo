#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// 08-pdfpig-reader.cs — the OTHER kind of IngestionDocumentReader, and why the boundary matters.
//
// Samples 06/07 use DocumentExtractionReader: a reader that is ALWAYS OCR (the whole document goes to an
// IDocumentExtractionClient). But most PDFs already carry a digital text layer — paying an OCR engine to re-read text
// that's already there is wasteful. PdfPig reads that native layer directly. The interesting shape is
// the hybrid: read native text first, and OCR ONLY the pages that have none (the scanned/image pages).
//
// This is CommunityToolkit/AI PR #14 in miniature — a single PdfPigReader with TWO clean pluggable
// seams (OCR is an injected enrichment, not the reader's identity, so the name stays PdfPigReader):
//
//   PdfPigReader : IngestionDocumentReader             <- a pipeline stage (the reader, #14)
//     seam 1  IPageSegmenter   -> HOW to segment a page (DefaultPageSegmenter heuristic <-> OnnxPageSegmenter)
//     seam 2  IDocumentExtractionClient + OcrPolicy -> WHEN to OCR (Never / FallbackForEmptyPages / AllPages), any engine
//
// The reader depends on IDocumentExtractionClient ONLY — zero IChatClient. The vision-LLM path is just one IDocumentExtractionClient
// provider (#15's VisionLMOcrClient), swappable with Mistral OCR / Azure DI on one line. That is the
// boundary from sample 05, made load-bearing: the reader owns "when to OCR", the client owns "how".
// See sample 13 + docs/composition-and-degradation.md for composing the two seams as a spectrum.
//
//   az login  (keyless)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet run 08-pdfpig-reader.cs -- data/usgs-petroleum-assessment.pdf

using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
var cred = new Azure.Identity.DefaultAzureCredential();

// The injected capability. Swap this line for VisionLMOcrClient (#15) or AzureDocumentIntelligenceClient
// and the reader below is untouched — that is the whole point of composing IDocumentExtractionClient.
using IDocumentExtractionClient ocr = new FoundryMistralOcrClient(new Uri(Require("OCR:FoundryEndpoint")), cred);

// --- 1) Never: native PdfPig text only. Zero OCR, zero network, zero IChatClient. -------------------
var native = new PdfPigReader(policy: OcrPolicy.Never);
IngestionDocument nativeDoc = await Read(native, pdf);
Console.WriteLine($"[Never]                 native PdfPig only  -> {nativeDoc.Sections.Count} pages, {Elements(nativeDoc)} elements, OCR calls: 0");

// --- 2) FallbackForEmptyPages: native first, OCR ONLY the pages with no digital text. ----------------
// the USGS fact sheet is born-digital, so every page has a text layer and the predicate never fires:
// the hybrid reader spends nothing on OCR. Feed it a scanned page and that page (and only that page)
// would route to the injected IDocumentExtractionClient.
var hybrid = new PdfPigReader(ocr, OcrPolicy.FallbackForEmptyPages);
IngestionDocument hybridDoc = await Read(hybrid, pdf);
Console.WriteLine($"[FallbackForEmptyPages] native + OCR gaps   -> {hybridDoc.Sections.Count} pages, {Elements(hybridDoc)} elements, OCR calls: {hybrid.OcrCalls} (all pages were digital)");

// --- 3) AllPages: hand the whole document to the IDocumentExtractionClient (document-native archetype). -------------
var ocrAll = new PdfPigReader(ocr, OcrPolicy.AllPages);
IngestionDocument ocrDoc = await Read(ocrAll, pdf);
Console.WriteLine($"[AllPages]              whole doc -> IDocumentExtractionClient -> {ocrDoc.Sections.Count} pages, {Elements(ocrDoc)} elements, OCR calls: {ocrAll.OcrCalls}");

// --- Same downstream pipeline, whichever reader produced the document. -------------------------------
Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
var chunker = new SectionChunker(new IngestionChunkerOptions(tokenizer)
{
    MaxTokensPerChunk = 256,
});
int chunks = 0;
await foreach (IngestionChunk _ in chunker.ProcessAsync(nativeDoc, default)) chunks++;
Console.WriteLine($"\nBoth readers feed the SAME chunker: [Never] document -> {chunks} chunks. One pipeline, two readers, one IDocumentExtractionClient seam.");
return 0;

static async Task<IngestionDocument> Read(IngestionDocumentReader reader, string path)
{
    await using FileStream s = File.OpenRead(path);
    return await reader.ReadAsync(s, Path.GetFileName(path), "application/pdf");
}
static int Elements(IngestionDocument d) => d.Sections.Sum(s => s.Elements.Count);
static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} via user-secrets (--id iocrclient-demo); values are never committed.");
