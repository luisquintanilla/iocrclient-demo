#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 01-vision-ocr.cs — a vision LLM behind IDocumentExtractionClient (the transcribe-by-seeing archetype).
//
// The contrast to the document-native engines: a general vision LLM reads the page visually and
// hands back one blob of Markdown. Newer models (gpt-4.1) accept a PDF directly, so the INPUT can be
// the same PDF the other providers take — but the OUTPUT is lower-fidelity (no native page model,
// no structured tables/bbox/confidence, nondeterministic). Same IDocumentExtractionClient contract; this is the
// honest home for "use a vision model to read" — a swappable provider, not a first-class reader.
//
//   az login
//   OCR_OPENAI_ENDPOINT=https://<account>.openai.azure.com OCR_VISION_DEPLOYMENT=gpt-4.1-mini \
//     dotnet run 01-vision-ocr.cs -- data/usgs-petroleum-assessment.pdf
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string endpoint = Require("OCR:OpenAIEndpoint");
string deployment = DemoOcr.DemoConfig.Config["OCR:VisionDeployment"] ?? "gpt-4.1-mini";
string image = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string mediaType = image.EndsWith(".pdf") ? "application/pdf"
    : image.EndsWith(".jpg") || image.EndsWith(".jpeg") ? "image/jpeg" : "image/png";

IChatClient chat = new AzureOpenAIClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

using IDocumentExtractionClient ocr = new VisionLlmOcrClient(chat);

await using FileStream doc = File.OpenRead(image);
DocumentExtractionResult result = await ocr.ExtractAsync(doc, mediaType);

Console.WriteLine($"model  : {result.GetModelId()}");
Console.WriteLine($"pages  : {result.Pages.Count}");
Console.WriteLine();
string md = result.Pages[0].Markdown
    ?? throw new InvalidOperationException("The provider did not return exact Markdown.");
Console.WriteLine("--- exact provider Markdown ---");
Console.WriteLine(md.Length > 900 ? md[..900] + "\n…" : md);
return 0;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
