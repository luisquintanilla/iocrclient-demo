#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 05-one-loop-four-clients.cs — the payoff. FOUR engines, FOUR wire protocols, ONE interface.
//
// Vision LLM, Mistral OCR, Azure Document Intelligence, Azure Content Understanding each speak a
// completely different API. Behind IDocumentExtractionClient they are IDocumentExtractionClient. The loop below is the ENTIRE
// consumer: same call, same DocumentExtractionResult, swap the provider with one line. That is the whole point of
// putting a seam here — your pipeline stops caring which engine read the page.
//
//   az login   # then set the endpoints (see .env header comments in the single-provider samples)
//   dotnet run 05-one-loop-four-clients.cs -- data/usgs-petroleum-assessment.pdf
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
var cred = new Azure.Identity.DefaultAzureCredential();

// The only provider-specific code in the whole program: constructing each client. After this line,
// nothing downstream knows or cares which engine it is talking to.
var clients = new (string Name, IDocumentExtractionClient Client)[]
{
    ("vision-llm (gpt-4.1-mini)", new VisionLlmOcrClient(
        new AzureOpenAIClient(new Uri(Require("OCR:OpenAIEndpoint")), cred)
            .GetChatClient(DemoOcr.DemoConfig.Config["OCR:VisionDeployment"] ?? "gpt-4.1-mini")
            .AsIChatClient())),
    ("mistral-ocr", new FoundryMistralOcrClient(new Uri(Require("OCR:FoundryEndpoint")), cred)),
    ("azure-document-intelligence", new AzureDocumentIntelligenceClient(new Uri(Require("OCR:DocIntelEndpoint")), cred)),
    ("azure-content-understanding", new ContentUnderstandingClient(new Uri(Require("OCR:ContentUnderstandingEndpoint")), cred)),
};

byte[] bytes = await File.ReadAllBytesAsync(pdf);

Console.WriteLine($"{"provider",-30} {"pages",6} {"tables",7} {"chars",8}  first-heading");
Console.WriteLine(new string('-', 90));
foreach (var (name, client) in clients)
{
    using (client)
    {
        // Identical call for every engine. This block never changes when you add or swap a provider.
        using var stream = new MemoryStream(bytes, writable: false);
        DocumentExtractionResult r = await client.ExtractAsync(stream, "application/pdf");

        int tables = r.Pages.Sum(p => p.Elements.OfType<DocumentTable>().Count());
        string providerOutput = string.Join(
            "\n\n",
            r.Pages.Select(page => page.GetProviderMarkdownOrCanonicalText()));
        int chars = providerOutput.Length;
        string heading = FirstHeading(providerOutput);
        Console.WriteLine($"{name,-30} {r.Pages.Count,6} {tables,7} {chars,8}  {heading}");
    }
}
return 0;

static string FirstHeading(string md)
{
    foreach (string line in md.Split('\n'))
    {
        string t = line.Trim();
        if (t.Length > 0)
        {
            return (t.Length > 40 ? t[..40] + "…" : t).Replace('#', ' ').Trim();
        }
    }

    return "";
}

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the single-provider sample headers).");
