#:project ocr-shape/OcrShape.csproj
#pragma warning disable EXTEXP0019, MEAI001, MEDE0001, MEAI002, MEAI003
// 16-streaming.cs — ExtractPagesAsync: pages arrive as observations, not one big result.
//
// The family idiom. Just as IChatClient.GetStreamingResponseAsync yields ChatResponseUpdates,
// IDocumentExtractionClient.ExtractPagesAsync yields one DocumentExtractionPage per page. A RAG
// pipeline can inspect page 1 while page 100 is still being read. Provider-specific progress
// metadata is not part of this shared contract.
//
// The page finalization extensions explicitly finalize the same pages into one
// Documents-backed DocumentExtractionResult. The page sequence is the required proof surface;
// any future JSON representation is a separate experiment, not the canonical shape.
//
//   az login                                # keyless: your identity needs a Cognitive Services role
//   OCR_FOUNDRY_ENDPOINT=https://<account>.services.ai.azure.com \
//     dotnet run 16-streaming.cs -- data/usgs-petroleum-assessment.pdf
//
// Config comes from env vars only (never committed): OCR_FOUNDRY_ENDPOINT (required),
// OCR_MISTRAL_MODEL (optional, defaults to mistral-ocr-4-0).
//
// NOTE (honest framing): this provider currently buffers its service response before yielding pages.
// The stream shape is real, while transport-level incrementality depends on the provider.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string endpoint = Require("OCR:FoundryEndpoint");
string model = DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IDocumentExtractionClient ocr = new FoundryMistralOcrClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential(), model);

await using FileStream doc = File.OpenRead(pdf);

Console.WriteLine("streaming pages as they arrive:");
List<DocumentExtractionPage> pages = [];
await foreach (DocumentExtractionPage page in ocr.ExtractPagesAsync(doc, "streaming-proof", "application/pdf"))
{
    pages.Add(page);
    Console.WriteLine($"  page {page.PageNumber,3}  {page.GetMarkdown().Length,6} chars");
}

using DocumentExtractionResult reassembled = pages.ToDocumentExtractionResult("streaming-proof");
Console.WriteLine();
Console.WriteLine($"reassembled: {reassembled.Pages.Count} page(s), elements={reassembled.EnumerateElements().Count()}");

return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
