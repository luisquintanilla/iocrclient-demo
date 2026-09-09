#:project ocr-shape/OcrShape.csproj
#:package OllamaSharp@5.4.25
#:property JsonSerializerIsReflectionEnabledByDefault=true
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 14-ollama-glm-ocr.cs — a LOCAL, open-weights OCR engine behind IDocumentExtractionClient (GLM-OCR on Ollama).
//
// The fifth engine, and the first that costs nothing and never leaves the machine. GLM-OCR (~0.9B,
// tops OmniDocBench) runs on Ollama and speaks the SAME IChatClient the cloud vision engines do — so
// it drops in behind the repo's existing VisionLlmOcrClient with NO new provider class. The entire
// integration is a few lines of MEAI composition on the Ollama IChatClient, and ocr-shape is untouched.
//
// Three glm-ocr realities, all patched by composition at THIS wiring site (never in the shared client):
//   1. it needs a task-prefix prompt ("Text Recognition:") — a generic prompt returns empty.
//      -> passed via VisionLlmOcrClient's existing `prompt` ctor arg.
//   2. it runs away emitting ``` fences to the token cap without a stop sequence.
//      -> injected with ConfigureOptions (works even on the null options the client passes).
//   3. Ollama's glm-ocr never emits a terminal done:true frame, so OllamaSharp's non-streaming
//      GetResponseAsync throws "did not yield an item with Done=true".
//      -> a ~3-line .Use(...) shim redirects THIS client's GetResponseAsync to the working streaming
//         path and aggregates. Cloud engines and VisionLlmOcrClient are unaffected.
//
// Ollama accepts IMAGES, not PDFs, so for a scanned PDF we pull the page's embedded image out with
// PdfPig — image EXTRACTION, not rasterization; PdfPig is already a repo dependency. (A born-digital
// PDF has a text layer and doesn't need OCR; this is the image-only case, OCR's home turf.)
//
//   ollama pull glm-ocr                       # the entire setup — no cloud, no az login, no secrets
//   dotnet run 14-ollama-glm-ocr.cs           # page 1 of the scanned USGS fact sheet (default)
//   dotnet run 14-ollama-glm-ocr.cs -- data/some-scan.png   # or any image / PDF path
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using OllamaSharp;
using UglyToad.PdfPig;
using DemoOcr;

string input = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment-scanned.pdf";
string host = DemoConfig.Get("OCR:OllamaEndpoint", "http://localhost:11434");
string model = DemoConfig.Get("OCR:OllamaOcrModel", "glm-ocr");

(byte[] imageBytes, string mediaType, string note) = LoadImage(input);
Console.WriteLine($"input  : {Path.GetFileName(input)}  ({note})");
Console.WriteLine($"engine : {model} on Ollama ({host}) — 100% local, GPU-backed, ~$0/page");
Console.WriteLine();

// Local GLM-OCR as an IChatClient. All three glm-ocr quirks above are patched HERE, on the Ollama
// client ONLY — VisionLlmOcrClient and the cloud engines stay byte-for-byte identical.
IChatClient chat = ((IChatClient)new OllamaApiClient(new Uri(host), model))
    .AsBuilder()                                                    // cast disambiguates AsBuilder (client is also IEmbeddingGenerator)
    .ConfigureOptions(o =>
    {
        o.Temperature ??= 0f;                                       // deterministic transcription
        (o.StopSequences ??= new List<string>()).Add("```");        // else glm-ocr runs away emitting fences
    })
    .Use(                                                           // Ollama glm-ocr never sends done:true, so the
        getResponseFunc: (messages, options, inner, ct) =>          // non-streaming GetResponseAsync throws; redirect
            inner.GetStreamingResponseAsync(messages, options, ct).ToChatResponseAsync(ct),
        getStreamingResponseFunc: null)                             // it to the working streaming path + aggregate.
    .Build();

// glm-ocr REQUIRES a task-prefix prompt; a generic prompt returns empty. The client is UNCHANGED.
using IDocumentExtractionClient ocr = new VisionLlmOcrClient(chat, "Text Recognition:");

var sw = Stopwatch.StartNew();
DocumentExtractionResult result = await ocr.ExtractAsync(new MemoryStream(imageBytes), mediaType);
sw.Stop();

string md = result.Pages.Count > 0 ? result.Pages[0].GetProviderMarkdownOrCanonicalText() : "";
Console.WriteLine($"model  : {result.GetModelId()}");
Console.WriteLine($"chars  : {md.Length}   [{sw.ElapsedMilliseconds} ms on local GPU, first call includes model load]");
Console.WriteLine();
Console.WriteLine("--- transcribed page (markdown) ---");
Console.WriteLine(md.Length > 1400 ? md[..1400] + "\n…" : md);
return 0;

// PdfPig image EXTRACTION (NOT rasterization): pull the page's embedded raster image straight out.
static (byte[] Bytes, string MediaType, string Note) LoadImage(string path)
{
    if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        using PdfDocument pdf = PdfDocument.Open(path);
        var img = pdf.GetPage(1).GetImages().FirstOrDefault();
        if (img is null)
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} page 1 has no embedded image to OCR. This sample targets " +
                "image-only (scanned) PDFs; a born-digital PDF has a text layer and doesn't need OCR.");
        if (img.TryGetPng(out byte[]? png) && png is not null)
            return (png, "image/png", "page 1 image extracted via PdfPig — no rasterizer");
        throw new NotSupportedException(
            $"Could not decode page 1's embedded image of {Path.GetFileName(path)} to PNG. " +
            "Pass a pre-rendered image path instead.");
    }
    string media = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
    return (File.ReadAllBytes(path), media, "image file");
}
