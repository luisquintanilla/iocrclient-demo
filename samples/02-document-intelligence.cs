#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 02-document-intelligence.cs — Azure AI Document Intelligence behind IDocumentExtractionClient (document-native).
//
// A DIFFERENT wire protocol from Mistral (async-poll AnalyzeResult, prebuilt-layout), but it
// normalizes onto the SAME DocumentExtractionResult: per-page Markdown, structured tables (row/column cells), and
// native paragraph polygons. Swap the engine, keep the pipeline. Keyless via DefaultAzureCredential.
//
//   az login
//   OCR_DI_ENDPOINT=https://<account>.cognitiveservices.azure.com \
//     dotnet run 02-document-intelligence.cs -- data/usgs-petroleum-assessment.pdf
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string endpoint = Require("OCR:DocIntelEndpoint");
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IDocumentExtractionClient ocr = new AzureDocumentIntelligenceClient(
    new Uri(endpoint), new Azure.Identity.DefaultAzureCredential());

await using FileStream doc = File.OpenRead(pdf);
DocumentExtractionResult result = await ocr.ExtractAsync(doc, "application/pdf");

Console.WriteLine("source : azure-document-intelligence");
Console.WriteLine($"model  : {result.GetModelId()}");
Console.WriteLine($"pages  : {result.Pages.Count}");
int tables = result.Pages.Sum(p => p.Elements.OfType<DocumentTable>().Count());
int blocks = result.Pages.Sum(p => p.Elements.OfType<DocumentBlock>().Count());
Console.WriteLine($"tables : {tables}   blocks : {blocks}");
Console.WriteLine();
string md = result.Pages[0].GetProviderMarkdownOrCanonicalText();
Console.WriteLine($"--- page {result.Pages[0].PageNumber} (provider markdown or canonical text) ---");
Console.WriteLine(md.Length > 900 ? md[..900] + "\n…" : md);
return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
