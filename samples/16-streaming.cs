#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 16-streaming.cs — ExtractPagesAsync: pages arrive as updates, not one big result.
//
// The family idiom. Just as IChatClient.GetStreamingResponseAsync yields ChatResponseUpdates,
// IDocumentExtractionClient.ExtractPagesAsync yields DocumentExtractionPageResult values, one per page
// as the engine finishes it. A RAG pipeline can chunk/embed page 1 while page 100 is still being read.
// TotalPages rides on the update, replacing the old
// IProgress<OcrProgress> parameter that used to hang off ExtractAsync.
//
// Then DocumentExtractionPageResultExtensions reduces the very same updates back into one DocumentExtractionResult —
// identical to what ExtractAsync would have returned — proving updates and result are two views of
// one thing (mirrors ChatResponseUpdate + ToChatResponseAsync()).
//
//   az login                                # keyless: your identity needs a Cognitive Services role
//   OCR_FOUNDRY_ENDPOINT=https://<account>.services.ai.azure.com \
//     dotnet run 16-streaming.cs -- data/usgs-petroleum-assessment.pdf
//
// Config comes from env vars only (never committed): OCR_FOUNDRY_ENDPOINT (required),
// OCR_MISTRAL_MODEL (optional, defaults to mistral-ocr-4-0).
//
// NOTE (honest framing): these demo extraction clients wrap a single ExtractAsync call and re-emit its
// pages as updates (DocumentExtractionDemoExtensions.StreamAsUpdates). The update *shape* is real; the
// incrementality is simulated. A polling engine (e.g. Azure Document Intelligence) would emit
// genuinely incremental page updates behind this exact same API.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string endpoint = Require("OCR:FoundryEndpoint");
string model = DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IDocumentExtractionClient ocr = new FoundryMistralOcrClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential(), model);

await using FileStream doc = File.OpenRead(pdf);

Console.WriteLine("streaming pages as they arrive:");
List<DocumentExtractionPageResult> updates = [];
await foreach (DocumentExtractionPageResult update in ocr.ExtractPagesAsync(doc, "application/pdf"))
{
    updates.Add(update);
    if (update.Page is { } page)
    {
        Console.WriteLine($"  page {page.PageNumber,3}  ({updates.Count}/{update.TotalPages})  {page.Text.Length,6} canonical chars");
    }
}

// Reduce the SAME updates back into one DocumentExtractionResult (the streaming twin is ToDocumentExtractionResultAsync() called
// directly on the IAsyncEnumerable; here we reduce the captured list to avoid a second call).
DocumentExtractionResult reassembled = updates.ToDocumentExtractionResult();
Console.WriteLine();
Console.WriteLine($"reassembled: {reassembled.Pages.Count} page(s), model={reassembled.GetModelId()}");

return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
