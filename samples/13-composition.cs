#:project ocr-shape/OcrShape.csproj
#pragma warning disable EXTEXP0019, MEAI001, MEDE0001, MEAI002, MEAI003
using DemoOcr;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

string pdf = "data/usgs-petroleum-assessment.pdf";
string layout = "native";
string? model = null;
string? hash = null;
bool allowLive = false;
for (int index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--layout" when index + 1 < args.Length: layout = args[++index]; break;
        case "--model" when index + 1 < args.Length: model = args[++index]; break;
        case "--model-sha256" when index + 1 < args.Length: hash = args[++index]; break;
        case "--allow-live": allowLive = true; break;
        case var argument when !argument.StartsWith("--", StringComparison.Ordinal): pdf = argument; break;
        default: throw new ArgumentException("Use <PDF> --layout native|rtdetr --model <path> --model-sha256 <published hash> and optional --allow-live.");
    }
}
if (layout is not ("native" or "rtdetr"))
{
    throw new ArgumentException("Layout must be native or rtdetr; no implicit fallback is selected.");
}
if (layout == "rtdetr" && (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(hash)))
{
    throw new InvalidOperationException("RT-DETR requires a compatible licensed model and its pinned SHA-256; inference has not run.");
}

using RtDetrLayoutDetector? detector = layout == "rtdetr"
    ? new RtDetrLayoutDetector(model!, expectedSha256: hash!) : null;
using IDocumentExtractionClient client = new PdfPigDocumentExtractionClient(
    rasterizer: detector is null ? null : new PdfPageRasterizer(dpi: 144),
    layoutDetector: detector);
var options = new DocumentExtractionOptions();
options.AdditionalProperties["layoutMode"] = layout;
var reader = new DocumentExtractionReader(client, options);
await using FileStream input = File.OpenRead(pdf);
using Document document = await reader.ReadAsync(input, Path.GetFileName(pdf), "application/pdf");
Console.WriteLine($"Local {layout}: {document.SourcePages.Count} source frames, {document.Sections.Count} sections.");
Console.WriteLine(detector is null
    ? "Native text/layout only. No model inference or OCR recognition was run."
    : "RT-DETR ran on actual rendered pages. Regions/scores are layout evidence, not recognized scan text or table-cell structure.");

if (allowLive)
{
    using IDocumentExtractionClient cloud = new FoundryMistralOcrClient(
        new Uri(DemoConfig.Require("OCR:FoundryEndpoint")), new Azure.Identity.DefaultAzureCredential());
    var hybrid = new PdfPigReader(cloud, OcrPolicy.FallbackForEmptyPages);
    input.Position = 0;
    using Document hybridDocument = await hybrid.ReadAsync(input, Path.GetFileName(pdf), "application/pdf");
    Console.WriteLine($"Explicit native/recognition policy: {hybridDocument.SourcePages.Count} source frames, {hybrid.OcrCalls} provider calls.");
}
else
{
    Console.WriteLine("Cloud recognition not selected. Missing text on scanned pages is not reported as recognized.");
}
return 0;
