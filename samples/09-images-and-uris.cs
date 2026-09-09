#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 09-images-and-uris.cs — two round-2 API prototypes, exercised live.
//
//  (A) DocumentImage as a first-class element. Figures surface in the page's reading-order Elements list
//      (no request toggle) via Elements.OfType<DocumentImage>(), and TWO document-native engines fill them:
//      Mistral OCR (inline base64 + bbox) and Azure Document Intelligence (cropped figure bytes + caption
//      + bbox via output=figures). Same shape, two engines.
//
//  (B) A UriContent overload for ExtractAsync. UriContent already exists in dotnet/extensions, so the
//      overload is symmetric with the shipped DataContent one. It resolves self-contained data: URIs and
//      leaves native URL passthrough as an explicit open question (see docs/api-notes.md).
//
//  (C) ExtractFromUriAsync — the opt-in remote downloader (R2). ExtractAsync(UriContent) never touches
//      the network (remote -> NotSupported); ExtractFromUriAsync is the explicit counterpart that GETs
//      http/https bytes with a CALLER-supplied HttpClient, then runs the normal stream extraction. We
//      serve the local PDF over loopback so this is a real http download with no external dependency.
//
//   az login
//   OCR_FOUNDRY_ENDPOINT=https://<account>.services.ai.azure.com \
//   OCR_DI_ENDPOINT=https://<account>.cognitiveservices.azure.com \
//     dotnet run 09-images-and-uris.cs -- data/usgs-petroleum-assessment.pdf
//
// Config comes from env vars only (never committed). Image bytes are written to output/images/
// (gitignored); only counts + bbox + captions are printed.
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DemoOcr;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
byte[] bytes = await File.ReadAllBytesAsync(pdf);
string outDir = Path.Combine("output", "images");
Directory.CreateDirectory(outDir);

Console.WriteLine($"document : {pdf}  ({bytes.Length:N0} bytes)\n");

// (A) Images across two document-native engines -------------------------------------------------
var options = new DocumentExtractionOptions { AdditionalProperties = new() { ["includeImages"] = true } };

await RunImages(
    "mistral-ocr",
    new FoundryMistralOcrClient(
        new Uri(Require("OCR:FoundryEndpoint")),
        new Azure.Identity.DefaultAzureCredential(),
        DemoOcr.DemoConfig.Config["OCR:MistralModel"] ?? "mistral-ocr-4-0"));

await RunImages(
    "azure-document-intelligence",
    new AzureDocumentIntelligenceClient(
        new Uri(Require("OCR:DocIntelEndpoint")),
        new Azure.Identity.DefaultAzureCredential()));

// (B) The UriContent overload, via a self-contained data: URI -----------------------------------
Console.WriteLine("--- UriContent overload (data: URI) ---");
string dataUri = $"data:application/pdf;base64,{Convert.ToBase64String(bytes)}";
var uriContent = new UriContent(dataUri, "application/pdf");
using IDocumentExtractionClient mistral = new FoundryMistralOcrClient(
    new Uri(Require("OCR:FoundryEndpoint")), new Azure.Identity.DefaultAzureCredential());
DocumentExtractionResult viaUri = await mistral.ExtractAsync(uriContent);
Console.WriteLine($"UriContent -> {viaUri.GetModelId()}: {viaUri.Pages.Count} page(s). " +
    "Same result, reached through the ergonomic UriContent entry point.");

// (C) Remote http URI via the opt-in ExtractFromUriAsync downloader (R2) -------------------------
// Serve the same PDF over loopback so the http path is exercised for real, self-contained.
Console.WriteLine("\n--- Remote http URI via ExtractFromUriAsync (opt-in download) ---");
int port = FreePort();
string prefix = $"http://localhost:{port}/";
using var listener = new System.Net.HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();
Task serve = Task.Run(async () =>
{
    System.Net.HttpListenerContext ctx = await listener.GetContextAsync();
    ctx.Response.ContentType = "application/pdf";
    ctx.Response.ContentLength64 = bytes.Length;
    await ctx.Response.OutputStream.WriteAsync(bytes);
    ctx.Response.Close();
});

using var http = new HttpClient();
var remote = new UriContent($"{prefix}doc.pdf", "application/pdf");
DocumentExtractionResult viaRemote = await mistral.ExtractFromUriAsync(remote, http);
await serve;
Console.WriteLine($"UriContent(http) -> {viaRemote.GetModelId()}: {viaRemote.Pages.Count} page(s). " +
    "Bytes fetched over http by ExtractFromUriAsync(httpClient), then extracted normally.");
return 0;

async Task RunImages(string label, IDocumentExtractionClient client)
{
    using (client as IDisposable)
    {
        await using var doc = new MemoryStream(bytes);
        DocumentExtractionResult r = await client.ExtractAsync(doc, "application/pdf", options);
        int imgCount = r.Pages.Sum(p => p.Elements.OfType<DocumentImage>().Count());
        Console.WriteLine($"--- {label}: {r.Pages.Count} page(s), {imgCount} image(s) ---");
        foreach (DocumentPage page in r.Pages)
        {
            List<DocumentImage> images = page.Elements.OfType<DocumentImage>().ToList();
            for (int i = 0; i < images.Count; i++)
            {
                DocumentImage img = images[i];
                string bbox = "bbox:none";
                if (img.BoundingRegion is { } br && br.GetBounds() is { } b)
                {
                    bbox = $"bbox[{b.Left},{b.Top},{b.Right},{b.Bottom}]";
                }
                string caption = string.IsNullOrEmpty(img.Caption) ? "" : $" caption=\"{img.Caption}\"";
                string saved = "no-bytes";
                if (img.Content is { } content)
                {
                    string ext = img.MediaType?.Split('/').Last() ?? "bin";
                    string file = Path.Combine(outDir, $"{label}-p{page.PageNumber}-{i}.{ext}");
                    await File.WriteAllBytesAsync(file, content.ToArray());
                    saved = $"{content.Length:N0}B -> {file}";
                }

                Console.WriteLine($"  p{page.PageNumber} img{i}: {bbox}{caption}  ({saved})");
            }
        }

        Console.WriteLine();
    }
}

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");

static int FreePort()
{
    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    probe.Start();
    int p = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return p;
}
