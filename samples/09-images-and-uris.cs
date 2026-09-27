#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 09-images-and-uris.cs — image observations without freezing a provider wire shape.
//
// The provisional contract keeps image observations in the in-memory Documents tree. This proof
// reports only portable facts: page count, image count, and whether an image has optional content.
// It does not print provider payloads, geometry, captions, URLs, or document-derived bytes.
//
// Provider adapters remain provider-specific. Run with credentials only when validating a live
// adapter; the deterministic contract proof is samples/17-deterministic-extraction.cs.
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
byte[] bytes = await File.ReadAllBytesAsync(pdf);

string? endpoint = DemoConfig.Config["OCR:FoundryEndpoint"];
if (endpoint is null)
{
    Console.WriteLine("TODO run against Foundry Mistral OCR: set OCR:FoundryEndpoint.");
    return 0;
}

using IDocumentExtractionClient client = new FoundryMistralOcrClient(
    new Uri(endpoint),
    new Azure.Identity.DefaultAzureCredential(),
    DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0",
    includeImages: false);

await using var document = new MemoryStream(bytes, writable: false);
using DocumentExtractionResult result = await client.ExtractAsync(document, "application/pdf");

IReadOnlyList<DocumentExtractionImage> images = result.Pages
    .SelectMany(static page => page.Observations)
    .OfType<DocumentExtractionImage>()
    .ToList();

Console.WriteLine($"pages : {result.Pages.Count}");
Console.WriteLine($"images: {images.Count}");
Console.WriteLine($"image content present: {images.Count(static image => image.Content is not null)}");
return 0;
