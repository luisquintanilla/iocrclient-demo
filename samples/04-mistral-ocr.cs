#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 04-mistral-ocr.cs — Mistral OCR on Azure AI Foundry behind IDocumentExtractionClient (document-native archetype).
//
// A purpose-built document-AI model: the whole PDF goes up in ONE call and comes back as an ordered
// list of pages (per-page Markdown + tables). No client-side page splitting, no chat prompt.
//
//   az login                                # keyless: your identity needs a Cognitive Services role
//   OCR_FOUNDRY_ENDPOINT=https://<account>.services.ai.azure.com \
//     dotnet run 04-mistral-ocr.cs -- data/usgs-petroleum-assessment.pdf
//
// Config comes from env vars only (never committed): OCR_FOUNDRY_ENDPOINT (required),
// OCR_MISTRAL_MODEL (optional, defaults to mistral-ocr-4-0).
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string endpoint = Require("OCR:FoundryEndpoint");
string model = DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IDocumentExtractionClient ocr = new FoundryMistralOcrClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential(), model);

await using FileStream doc = File.OpenRead(pdf);
DocumentExtractionResult result = await ocr.ExtractAsync(doc, "application/pdf");

Report(result);
return 0;

static void Report(DocumentExtractionResult r)
{
    Console.WriteLine("source : mistral-ocr");
    Console.WriteLine($"model  : {r.GetModelId()}");
    Console.WriteLine($"pages  : {r.Pages.Count}");
    Console.WriteLine();
    DocumentPage first = r.Pages[0];
    Console.WriteLine($"--- page {first.PageNumber} provider markdown  ({first.Elements.OfType<DocumentTable>().Count()} table(s)) ---");
    string md = first.GetProviderMarkdownOrCanonicalText();
    Console.WriteLine(md.Length > 900 ? md[..900] + "\n…" : md);
}

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
