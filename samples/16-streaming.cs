#:project ocr-shape/OcrShape.csproj
// 16-streaming.cs — ExtractStreamingAsync: pages arrive as updates, not one big result.
//
// The family idiom. Just as IChatClient.GetStreamingResponseAsync yields ChatResponseUpdates,
// IOcrClient.ExtractStreamingAsync yields OcrResponseUpdates — one per page as the engine finishes
// it. A RAG pipeline can chunk/embed page 1 while page 100 is still being read. Progress
// (PagesProcessed / TotalPages / Status) now rides ON the update, replacing the old
// IProgress<OcrProgress> parameter that used to hang off ExtractAsync.
//
// Then OcrResponseUpdateExtensions reduces the very same updates back into one OcrResult —
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
// NOTE (honest framing): these demo IOcrClients wrap a single ExtractAsync call and re-emit its
// pages as updates (OcrShapeExtensions.StreamAsUpdates) — the update *shape* is real, the
// incrementality is simulated. A polling engine (e.g. Azure Document Intelligence) would emit
// genuinely incremental page updates behind this exact same API.
using Microsoft.Extensions.AI;
using DemoOcr;

string endpoint = Require("OCR:FoundryEndpoint");
string model = DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IOcrClient ocr = new FoundryMistralOcrClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential(), model);

await using FileStream doc = File.OpenRead(pdf);

Console.WriteLine("streaming pages as they arrive:");
List<OcrResponseUpdate> updates = [];
await foreach (OcrResponseUpdate update in ocr.ExtractStreamingAsync(doc, "application/pdf"))
{
    updates.Add(update);
    if (update.Page is { } page)
    {
        Console.WriteLine($"  page {page.PageNumber,3}  ({update.PagesProcessed}/{update.TotalPages})  {page.Markdown.Length,6} chars");
    }
    else
    {
        Console.WriteLine($"  done       status={update.Status ?? "-"}  model={update.ModelId}");
    }
}

// Reduce the SAME updates back into one OcrResult (the streaming twin is ToOcrResultAsync() called
// directly on the IAsyncEnumerable; here we reduce the captured list to avoid a second call).
OcrResult reassembled = updates.ToOcrResult();
Console.WriteLine();
Console.WriteLine($"reassembled: {reassembled.Pages.Count} page(s), model={reassembled.ModelId}");

return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
