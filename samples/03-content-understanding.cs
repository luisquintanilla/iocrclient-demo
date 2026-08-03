#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 03-content-understanding.cs — Azure AI Content Understanding behind IDocumentExtractionClient (document-native).
//
// A THIRD engine, a THIRD wire protocol (analyzer + async-poll AnalysisResult), same DocumentExtractionResult. CU is
// the widest-surface peer: one service can emit Markdown (this path, IDocumentExtractionClient) OR typed fields +
// grounding. Here we use the Markdown path so it drops into the same reader/RAG pipeline as the others.
// Needs a CU-supported region (West US, West US 3, Sweden Central, Australia East). Keyless.
//
//   az login
//   OCR_CU_ENDPOINT=https://<account>.services.ai.azure.com \
//     dotnet run 03-content-understanding.cs -- data/usgs-petroleum-assessment.pdf
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string endpoint = Require("OCR:ContentUnderstandingEndpoint");
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using IDocumentExtractionClient ocr = new ContentUnderstandingClient(
    new Uri(endpoint), new Azure.Identity.DefaultAzureCredential());

await using FileStream doc = File.OpenRead(pdf);
DocumentExtractionResult result = await ocr.ExtractAsync(doc, "application/pdf");

Console.WriteLine($"model  : {result.GetModelId()}");
Console.WriteLine($"pages  : {result.Pages.Count}");
Console.WriteLine();
string md = result.Pages[0].Text;
Console.WriteLine("--- page 0 (markdown) ---");
Console.WriteLine(md.Length > 900 ? md[..900] + "\n…" : md);
return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
