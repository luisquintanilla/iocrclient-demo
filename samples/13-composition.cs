#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3

// 13-composition.cs — the extraction spectrum, as COMPOSITION (not a new type).
//
// Graceful "degradation" is not a FallbackOcrClient or a PdfPigHeuristicOcrClient — it is choosing
// how to COMPOSE the same PdfPigReader's two pluggable seams for the rung your environment/document
// supports, and falling inward when a model or service isn't available. The building blocks already
// exist; this sample composes them three ways over ONE document and prints the trade-offs.
//
//   seam 1  IPageSegmenter          — HOW to segment a page
//   seam 2  IOcrClient + OcrPolicy   — WHEN/whether to OCR
//
// The spectrum, cheapest/local -> richest/service:
//
//   1. native text + heuristic layout   DefaultPageSegmenter, OcrPolicy.Never      (local, zero ML, zero net)
//   2. native text + ML layout          OnnxPageSegmenter,    OcrPolicy.Never      (local ML, PR 3 prior art)
//   3. native + OCR the scanned pages    DefaultPageSegmenter, OcrPolicy.Fallback…  (service only where needed)
//   4. whole-doc OCR                     OcrPolicy.AllPages                          (document-native engine)
//
// "Degrade" = pick the rung you can run; fall inward when a rung's model/service is missing. Rung 2's
// OnnxPageSegmenter (CommunityToolkit/AI PR 3, PdfPig.OnnxLayoutAnalysis, an RtDetr layout model) is
// shown as the documented rung: if its ONNX model file is present we plug it into the SAME seam; if
// not, we fall inward to rung 1 — which is exactly the degradation story. See
// docs/composition-and-degradation.md.
//
//   az login  (keyless, only needed for rungs 3-4)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet run 13-composition.cs -- data/usgs-petroleum-assessment.pdf

using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

Console.WriteLine($"Composing PdfPigReader over {Path.GetFileName(pdf)} — same reader, different rungs:\n");

// --- Rung 1: native text + heuristic layout. Local, zero ML, zero network, zero IChatClient. --------
var rung1 = new PdfPigReader(policy: OcrPolicy.Never, pageSegmenter: DefaultPageSegmenter.Instance);
await Report("1. native + heuristic layout   (DefaultPageSegmenter, Never)", rung1, pdf);

// --- Rung 2: native text + ML layout via OnnxPageSegmenter (PR 3). Same seam, ML model swapped in. --
// The ONNX layout model isn't bundled here, so this rung is the DOCUMENTED one: if an
// IPageSegmenter-implementing OnnxPageSegmenter + its model are available, plug it into the SAME
// pageSegmenter seam; otherwise fall inward to rung 1. That fallback IS the degradation.
IPageSegmenter? onnx = TryLoadOnnxSegmenter();
if (onnx is not null)
{
    var rung2 = new PdfPigReader(policy: OcrPolicy.Never, pageSegmenter: onnx);
    await Report("2. native + ONNX layout        (OnnxPageSegmenter, Never)", rung2, pdf);
}
else
{
    Console.WriteLine("2. native + ONNX layout        (OnnxPageSegmenter, Never)");
    Console.WriteLine("     -> ONNX layout model not present in this environment; degrade inward to rung 1.");
    Console.WriteLine("        Wire CommunityToolkit/AI PR 3 PdfPig.OnnxLayoutAnalysis into the SAME seam to enable.\n");
}

// --- Rung 3: native first, OCR only the scanned pages (the injected IOcrClient earns its keep). -----
// the USGS fact sheet is born-digital, so no page routes to OCR and OcrCalls stays 0 — the reader spends
// nothing. Needs an IOcrClient; only constructed if a Foundry endpoint is configured.
string? foundry = DemoOcr.DemoConfig.Config["OCR:FoundryEndpoint"];
if (foundry is not null)
{
    using IOcrClient ocr = new FoundryMistralOcrClient(new Uri(foundry), new Azure.Identity.DefaultAzureCredential());
    var rung3 = new PdfPigReader(ocr, OcrPolicy.FallbackForEmptyPages);
    await Report("3. native + OCR scanned pages  (FallbackForEmptyPages)", rung3, pdf, () => rung3.OcrCalls);
}
else
{
    Console.WriteLine("3. native + OCR scanned pages  (FallbackForEmptyPages)");
    Console.WriteLine("     -> no OCR:FoundryEndpoint configured; degrade inward to rung 1 (native only).\n");
}

Console.WriteLine("One reader, two seams. 'Degradation' = compose the rung you can run and fall inward — no new type.");
return 0;

async Task Report(string label, PdfPigReader reader, string path, Func<int>? ocrCalls = null)
{
    await using FileStream s = File.OpenRead(path);
    IngestionDocument doc = await reader.ReadAsync(s, Path.GetFileName(path), "application/pdf");
    int elements = doc.Sections.Sum(sec => sec.Elements.Count);
    string calls = ocrCalls is null ? "0 (local)" : ocrCalls().ToString();
    Console.WriteLine(label);
    Console.WriteLine($"     -> {doc.Sections.Count} pages, {elements} elements, OCR calls: {calls}\n");
}

// The degradation seam in one method: try to construct the ONNX-backed IPageSegmenter (PR 3); if its
// assembly/model isn't available in this environment, return null so the caller falls inward to rung 1.
static IPageSegmenter? TryLoadOnnxSegmenter() => null;
