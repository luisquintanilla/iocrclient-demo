#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.Extensions.Logging.Console@10.0.9
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003

// Optional provider-backed tier:
//   Mistral IDocumentExtractionClient -> built-in DocumentExtractionReader
//     -> shared Document tree -> current MEDI SectionChunker
//
// Configuration stays outside the repo:
//   az login
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo
//   dotnet run 06-medi-pipeline.cs -- data/usgs-petroleum-assessment.pdf

using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;
using Microsoft.ML.Tokenizers;

string endpoint = DemoConfig.Require("OCR:FoundryEndpoint");
string model = DemoConfig.Get("OCR:MistralModel", "mistral-ocr-4-0");
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";

using ILoggerFactory loggerFactory = LoggerFactory.Create(
    builder => builder.SetMinimumLevel(LogLevel.Warning).AddConsole());
using IDocumentExtractionClient extractionClient =
    new FoundryMistralOcrClient(new Uri(endpoint), new DefaultAzureCredential(), model)
        .AsBuilder()
        .UseLogging(loggerFactory)
        .Build();

var reader = new DocumentExtractionReader(extractionClient);
await using FileStream source = File.OpenRead(pdf);
IngestionDocument ingestion = await reader.ReadAsync(
    source,
    identifier: Path.GetFileName(pdf),
    mediaType: "application/pdf");

Console.WriteLine(
    $"=== shared Document: {ingestion.Document.Children.Count} root(s), {ingestion.Document.Nodes.Count} node(s) ===");
foreach (DocumentNode node in ingestion.Document.Nodes.Take(4))
{
    Console.WriteLine(
        $"{node.Id.Value} : {node.GetType().Name} pages=[{string.Join(',', node.PageReferences.Select(page => page.PageNumber))}]");
}

var chunker = new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 512,
});
var chunks = new List<IngestionChunk>();
await foreach (IngestionChunk chunk in chunker.ProcessAsync(ingestion))
{
    chunks.Add(chunk);
}

Console.WriteLine($"\n=== current MEDI chunker: {chunks.Count} chunk(s) ===");
Console.WriteLine($"all chunks retain typed source node IDs: {chunks.All(chunk => chunk.SourceNodeIds.Count > 0)}");
Console.WriteLine($"pages represented: {string.Join(", ", chunks.SelectMany(chunk => chunk.PageNumbers).Distinct().Order())}");

IngestionChunk? first = chunks.FirstOrDefault();
if (first is not null)
{
    Console.WriteLine($"first chunk pages=[{string.Join(',', first.PageNumbers)}]");
    Console.WriteLine($"first chunk source=[{string.Join(',', first.SourceNodeIds.Select(id => id.Value))}]");
    string text = first.Content is TextContent content
        ? content.Text
        : first.Content.ToString() ?? string.Empty;
    Console.WriteLine($"first chunk text={Trim(text.Replace('\n', ' '), 120)}");
}

return 0;

static string Trim(string value, int length) =>
    value.Length <= length ? value : value[..length] + "...";
