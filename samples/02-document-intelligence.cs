#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 02-document-intelligence.cs — Azure AI Document Intelligence behind IDocumentExtractionClient (document-native).
//
// A DIFFERENT wire protocol from Mistral (async-poll AnalyzeResult, prebuilt-layout), but it
// normalizes onto the SAME DocumentExtractionResult: canonical shared nodes, structured tables
// (row/column cells), and native paragraph polygons in evidence. The SDK exposes one document-level
// Markdown artifact, so page.Markdown remains accurately unavailable.
//
//   az login
//   OCR_DI_ENDPOINT=https://<account>.cognitiveservices.azure.com \
//     dotnet run 02-document-intelligence.cs -- data/usgs-petroleum-assessment.pdf
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using DemoOcr;

string endpoint = Require("OCR:DocIntelEndpoint");
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IDocumentExtractionClient ocr = new AzureDocumentIntelligenceClient(
    new Uri(endpoint), new Azure.Identity.DefaultAzureCredential());

await using FileStream doc = File.OpenRead(pdf);
DocumentExtractionResult result = await ocr.ExtractAsync(doc, "application/pdf");

Console.WriteLine($"model  : {result.GetModelId()}");
Console.WriteLine($"pages  : {result.Pages.Count}");
int tables = result.Document.Nodes.OfType<DocumentTable>().Count();
int blocks = result.Document.Nodes.OfType<DocumentText>().Count();
Console.WriteLine($"tables : {tables}   blocks : {blocks}");
Console.WriteLine();
string canonicalText = result.Pages[0].Text;
Console.WriteLine("--- deterministic canonical page projection ---");
Console.WriteLine(canonicalText.Length > 900 ? canonicalText[..900] + "\n…" : canonicalText);
return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
