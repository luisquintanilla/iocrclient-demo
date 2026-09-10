#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// Supported composition choices: native text with a selectable page segmenter, or whole-document
// extraction through an injected IDocumentExtractionClient. Per-page OCR fallback is not implemented
// because this demo does not include a PDF rasterizer.

using DemoOcr;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DocumentExtraction;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

var native = new PdfPigReader(
    policy: OcrPolicy.Never,
    pageSegmenter: DefaultPageSegmenter.Instance);
await Report("native + heuristic layout", native, pdf);

IPageSegmenter? onnx = TryLoadOnnxSegmenter();
if (onnx is not null)
{
    await Report("native + ONNX layout", new PdfPigReader(policy: OcrPolicy.Never, pageSegmenter: onnx), pdf);
}
else
{
    Console.WriteLine("native + ONNX layout: model not present; no fallback behavior is implied.");
}

string? foundryEndpoint = DemoConfig.Config["OCR:FoundryEndpoint"];
if (!string.IsNullOrWhiteSpace(foundryEndpoint))
{
    using IDocumentExtractionClient extractionClient = new FoundryMistralOcrClient(
        new Uri(foundryEndpoint),
        new Azure.Identity.DefaultAzureCredential());
    var wholeDocument = new PdfPigReader(extractionClient, OcrPolicy.AllPages);
    await Report("whole-document extraction", wholeDocument, pdf);
}
else
{
    Console.WriteLine("whole-document extraction: skipped because OCR:FoundryEndpoint is not configured.");
}

return 0;

static async Task Report(string label, PdfPigReader reader, string path)
{
    await using FileStream source = File.OpenRead(path);
    IngestionDocument document = await reader.ReadAsync(
        source,
        Path.GetFileName(path),
        "application/pdf");
    int pages = document.Document.Nodes
        .SelectMany(node => node.PageReferences)
        .Select(reference => reference.PageNumber)
        .Distinct()
        .Count();
    Console.WriteLine($"{label}: {pages} pages, {document.Document.Nodes.Count} shared nodes");
}

static IPageSegmenter? TryLoadOnnxSegmenter() => null;
