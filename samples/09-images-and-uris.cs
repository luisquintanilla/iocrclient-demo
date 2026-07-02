#:project ocr-shape/OcrShape.csproj
// 09-images-and-uris.cs — two round-2 API prototypes, exercised live.
//
//  (A) OcrOptions.IncludeImages -> OcrPage.Images. A request flag (IncludeImages) shipped in #7588,
//      but OcrPage had no sink for the result. This adds OcrImage + OcrPage.Images and wires TWO
//      document-native engines to fill it: Mistral OCR (inline base64 + bbox) and Azure Document
//      Intelligence (cropped figure bytes + caption + bbox via output=figures). Same shape, two engines.
//
//  (B) A UriContent overload for ExtractAsync. UriContent already exists in dotnet/extensions, so the
//      overload is symmetric with the shipped DataContent one. It resolves self-contained data: URIs and
//      leaves native URL passthrough as an explicit open question (see docs/api-notes.md).
//
//   az login
//   OCR_FOUNDRY_ENDPOINT=https://<account>.services.ai.azure.com \
//   OCR_DI_ENDPOINT=https://<account>.cognitiveservices.azure.com \
//     dotnet run 09-images-and-uris.cs -- data/usgs-petroleum-assessment.pdf
//
// Config comes from env vars only (never committed). Image bytes are written to output/images/
// (gitignored); only counts + bbox + captions are printed.
using Microsoft.Extensions.AI;
using DemoOcr;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
byte[] bytes = await File.ReadAllBytesAsync(pdf);
string outDir = Path.Combine("output", "images");
Directory.CreateDirectory(outDir);

Console.WriteLine($"document : {pdf}  ({bytes.Length:N0} bytes)\n");

// (A) Images across two document-native engines -------------------------------------------------
var options = new OcrOptions { IncludeImages = true };

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
using IOcrClient mistral = new FoundryMistralOcrClient(
    new Uri(Require("OCR:FoundryEndpoint")), new Azure.Identity.DefaultAzureCredential());
OcrResult viaUri = await mistral.ExtractAsync(uriContent);
Console.WriteLine($"UriContent -> {viaUri.OcrSource}: {viaUri.Pages.Count} page(s). " +
    "Same result, reached through the ergonomic UriContent entry point.");
return 0;

async Task RunImages(string label, IOcrClient client)
{
    using (client as IDisposable)
    {
        await using var doc = new MemoryStream(bytes);
        OcrResult r = await client.ExtractAsync(doc, "application/pdf", options);
        int imgCount = r.Pages.Sum(p => p.Images.Count);
        Console.WriteLine($"--- {label}: {r.Pages.Count} page(s), {imgCount} image(s) ---");
        foreach (OcrPage page in r.Pages)
        {
            for (int i = 0; i < page.Images.Count; i++)
            {
                OcrImage img = page.Images[i];
                string bbox = img.BoundingRegion is { } br
                    ? $"bbox[{string.Join(",", br.GetBounds())}]" : "bbox:none";
                string caption = string.IsNullOrEmpty(img.Caption) ? "" : $" caption=\"{img.Caption}\"";
                string saved = "no-bytes";
                if (img.Content is { } content)
                {
                    string ext = content.MediaType?.Split('/').Last() ?? "bin";
                    string file = Path.Combine(outDir, $"{label}-p{page.Index}-{i}.{ext}");
                    await File.WriteAllBytesAsync(file, content.Data.ToArray());
                    saved = $"{content.Data.Length:N0}B -> {file}";
                }

                Console.WriteLine($"  p{page.Index} img{i}: {bbox}{caption}  ({saved})");
            }
        }

        Console.WriteLine();
    }
}

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");
