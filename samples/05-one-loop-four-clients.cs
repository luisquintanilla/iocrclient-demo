#:project ocr-shape/OcrShape.csproj
#:package Azure.AI.OpenAI@2.5.0-beta.1
#:package Microsoft.Extensions.AI.OpenAI@[10.10.0-dev.e2e.cf60fa88.1]
#pragma warning disable EXTEXP0019, MEAI001, MEDE0001, MEAI002, MEAI003
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using DemoOcr;

string pdf = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal))
    ?? "data/usgs-petroleum-assessment.pdf";
if (args.Any(argument => argument.StartsWith("--", StringComparison.Ordinal) && argument is not ("--allow-live" or "--no-raw")))
{
    throw new ArgumentException("Use an optional PDF path, --allow-live and --no-raw.");
}
bool live = args.Contains("--allow-live", StringComparer.Ordinal);
var credential = new Azure.Identity.DefaultAzureCredential();
using IChatClient? chat = live && Config("OCR:OpenAIEndpoint") is { } openAi
    ? new AzureOpenAIClient(new Uri(openAi), credential)
        .GetChatClient(Config("OCR:VisionDeployment") ?? "gpt-4.1-mini").AsIChatClient()
    : null;
var clients = new List<(string Name, IDocumentExtractionClient Client)>
{
    ("pdfpig-native", new PdfPigDocumentExtractionClient()),
};
try
{
    if (live)
    {
        if (chat is not null) clients.Add(("vision", new VisionLlmOcrClient(chat)));
        if (Config("OCR:FoundryEndpoint") is { } mistral) clients.Add(("mistral", new FoundryMistralOcrClient(new Uri(mistral), credential)));
        if (Config("OCR:DocIntelEndpoint") is { } di) clients.Add(("document-intelligence", new AzureDocumentIntelligenceClient(new Uri(di), credential)));
        if (Config("OCR:ContentUnderstandingEndpoint") is { } cu) clients.Add(("content-understanding", new ContentUnderstandingClient(new Uri(cu), credential)));
    }
    byte[] bytes = await File.ReadAllBytesAsync(pdf);
    Console.WriteLine("Only constructed providers run. Unconfigured cloud providers and local model inference are not passing evidence.");
    Console.WriteLine($"{"provider",-25} {"observed pages",14} {"source frames",13} {"tables",7} {"chars",8}");
    foreach (var (name, client) in clients)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var options = new DocumentExtractionOptions { CaptureRawOutput = !args.Contains("--no-raw", StringComparer.Ordinal) };
        string identifier = Path.GetFileName(pdf);
        using DocumentExtractionResult result = await client.ExtractPagesAsync(stream, identifier, "application/pdf", options)
            .ToDocumentExtractionResultAsync(identifier);
        Console.WriteLine($"{name,-25} {result.Pages.Count,14} {result.Document.SourcePages.Count,13} {result.CountElements<DocumentTableElement>(),7} {result.GetMarkdown().Length,8}");
    }
}
finally
{
    foreach (var (_, client) in clients) client.Dispose();
}
return 0;

static string? Config(string key) => DemoConfig.Config[key];
