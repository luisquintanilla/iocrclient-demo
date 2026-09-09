#:project ocr-shape/OcrShape.csproj
#:project bench/OcrBench/OcrBench.csproj
#:package OllamaSharp@5.4.25
#:property JsonSerializerIsReflectionEnabledByDefault=true
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 15-ocr-engine-comparison.cs — cloud vs local OCR through ONE interface (the blog's cost/locality cut).
//
// Azure Document Intelligence vs Mistral OCR vs GLM-OCR (Ollama, local) on the SAME scanned document,
// all reached through the same IDocumentExtractionClient seam and the same bench Harness. The point isn't a winner —
// it's that swapping a cloud engine for a free, offline, local model is a one-line change of which
// IDocumentExtractionClient you construct. GLM-OCR (~0.9B, tops OmniDocBench) runs on your GPU at ~$0/page; the cloud
// engines are document-native and structured. Same call site, three very different tradeoffs.
//   (motivation: BlueGuardrails, "High-Throughput VLM OCR" — GLM-OCR ~$0.04 vs Mistral ~$4 vs DI ~$10 / 1k pages)
//
// Input asymmetry, honestly handled: cloud engines rasterize server-side and take the whole PDF; Ollama
// wants images, so the local engine is wrapped in an inline PdfImageOcrClient that pulls each page's
// embedded image out with PdfPig (image EXTRACTION, not rasterization — no new dependency). ocr-shape
// and VisionLlmOcrClient are untouched; the two glm-ocr quirks + the Ollama done-frame bug are patched
// by composition on the Ollama IChatClient only (see BuildOllamaChat below and sample 14 for the why).
//
//   az login                                  # for the two cloud engines (skipped gracefully if unset)
//   ollama pull glm-ocr                       # for the local engine
//   dotnet run 15-ocr-engine-comparison.cs                              # scanned USGS fact sheet (default)
//   dotnet run 15-ocr-engine-comparison.cs -- data/usgs-petroleum-assessment.pdf
using System.Text;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using OllamaSharp;
using UglyToad.PdfPig;
using DemoOcr;
using OcrBench;

string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment-scanned.pdf";
const string mediaType = "application/pdf";
var cred = new DefaultAzureCredential();

Console.WriteLine($"document : {Path.GetFileName(pdf)}  (scanned / image-only — OCR's home turf)");
Console.WriteLine("interface: Microsoft.Extensions.DocumentExtraction.IDocumentExtractionClient: one seam, three engines\n");

string host = DemoConfig.Get("OCR:OllamaEndpoint", "http://localhost:11434");
string glmModel = DemoConfig.Get("OCR:OllamaOcrModel", "glm-ocr");

// engine name | locality | cited list $/1k pages | factory (null => not configured, graceful skip)
var engines = new List<(string Name, string Locality, double ListPer1k, Func<IDocumentExtractionClient>? Make)>
{
    ("azure-di", "cloud", 10.00,
        Opt("OCR:DocIntelEndpoint") is { } di ? () => new AzureDocumentIntelligenceClient(new Uri(di), cred) : null),
    ("mistral-ocr", "cloud", 4.00,
        Opt("OCR:FoundryEndpoint") is { } mf ? () => new FoundryMistralOcrClient(new Uri(mf), cred) : null),
    ("glm-ocr (local)", "local", 0.04,
        () => new PdfImageOcrClient(BuildOllamaChat(host, glmModel), "Text Recognition:")),
};

var rows = new List<(string Name, string Locality, double ListPer1k, ExtractionOutcome? Outcome)>();
foreach ((string name, string locality, double per1k, Func<IDocumentExtractionClient>? make) in engines)
{
    if (make is null)
    {
        Console.WriteLine($"--- {name}: skipped (endpoint not configured) ---\n");
        rows.Add((name, locality, per1k, null));
        continue;
    }

    Console.WriteLine($"--- {name} ({locality}) ---");
    try
    {
        using IDocumentExtractionClient ocr = make();
        ExtractionOutcome o = await Harness.ExtractWithOcrAsync(name, ocr, pdf, mediaType);
        Console.WriteLine($"  pages={o.PageCount}  tables={o.TableCount}  figures={o.ImageCount}  chars={o.Text.Length}  [{o.ElapsedMs} ms]");
        Console.WriteLine($"  \"{Snippet(o.Text)}\"\n");
        rows.Add((name, locality, per1k, o));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  failed: {ex.GetType().Name}: {ex.Message}\n");
        rows.Add((name, locality, per1k, null));
    }
}

var table = new StringBuilder();
table.AppendLine("| Engine | Locality | Latency | Pages | Tables | Figures | List $/1k pages | Est. $ (this doc) |");
table.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
foreach ((string name, string locality, double per1k, ExtractionOutcome? o) in rows)
{
    if (o is null)
    {
        table.AppendLine($"| {name} | {locality} | (skipped) | — | — | — | ${per1k:0.00} | — |");
        continue;
    }
    double est = per1k / 1000.0 * o.PageCount;
    table.AppendLine($"| {name} | {locality} | {o.ElapsedMs} ms | {o.PageCount} | {o.TableCount} | {o.ImageCount} | ${per1k:0.00} | ${est:0.0000} |");
}

Console.WriteLine("=== comparison (same IDocumentExtractionClient, same Harness) ===");
Console.WriteLine(table.ToString());
Console.WriteLine("notes:");
Console.WriteLine("  - latency is wall-clock on THIS machine (local GPU vs cloud round-trip on different hardware),");
Console.WriteLine("    a tradeoff illustration — NOT an 'X beats Y' verdict.");
Console.WriteLine("  - list $/1k pages are published/cited prices (BlueGuardrails, 2026), not measured; verify current");
Console.WriteLine("    vendor pricing. GLM-OCR's cost is self-hosted GPU amortization, effectively ~$0 at the margin.");
Console.WriteLine("  - the document-native cloud engines return structured tables/figures; GLM-OCR is a vision LLM");
Console.WriteLine("    returning freeform Markdown (0 tables/figures is honest for that path — it transcribes, it");
Console.WriteLine("    doesn't parse structure). Same DocumentExtractionResult, different fidelity — the tradeoff the one seam unlocks.");
return 0;

static string Snippet(string text)
{
    string s = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return s.Length > 160 ? s[..160] + "…" : s;
}

static IChatClient BuildOllamaChat(string host, string model) =>
    ((IChatClient)new OllamaApiClient(new Uri(host), model))
        .AsBuilder()
        .ConfigureOptions(o =>
        {
            o.Temperature ??= 0f;
            (o.StopSequences ??= new List<string>()).Add("```");
        })
        .Use(                                                   // Ollama glm-ocr never sends done:true -> non-streaming
            getResponseFunc: (messages, options, inner, ct) =>  // GetResponseAsync throws; redirect to streaming + aggregate.
                inner.GetStreamingResponseAsync(messages, options, ct).ToChatResponseAsync(ct),
            getStreamingResponseFunc: null)
        .Build();

static string? Opt(string key) => DemoConfig.Config[key];

/// <summary>
/// PDF -> per-page-image adapter so an image-only engine (Ollama glm-ocr) fits the same PDF-in IDocumentExtractionClient
/// call the cloud engines take. Pulls each page's embedded raster image out with PdfPig (image EXTRACTION,
/// not rasterization — no new dependency), runs the inner image-only VisionLlmOcrClient per page, and
/// aggregates into one DocumentExtractionResult with correct page indices. Freeform vision output => 0 tables/figures.
/// </summary>
sealed class PdfImageOcrClient(IChatClient chat, string prompt) : IDocumentExtractionClient
{
    private readonly VisionLlmOcrClient _inner = new(chat, prompt);

    public async Task<DocumentExtractionResult> ExtractAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var pages = new List<DocumentPage>();
        string? modelId = null;
        using PdfDocument pdf = PdfDocument.Open(buffer.ToArray());
        int total = pdf.NumberOfPages;
        for (int i = 1; i <= total; i++)
        {
            var img = pdf.GetPage(i).GetImages().FirstOrDefault();
            if (img is null || !img.TryGetPng(out byte[]? png) || png is null)
            {
                pages.Add(new DocumentPage(i, []));   // no extractable image on this page
                continue;
            }

            DocumentExtractionResult one = await _inner
                .ExtractAsync(new MemoryStream(png), "image/png", options, cancellationToken)
                .ConfigureAwait(false);
            modelId ??= one.GetModelId();
            DocumentPage sourcePage = one.Pages[0];
            pages.Add(new DocumentPage(i, sourcePage.Elements, sourcePage.Markdown));
        }

        return new DocumentExtractionResult(pages)
        {
            AdditionalProperties = modelId is { Length: > 0 } ? new() { ["modelId"] = modelId } : null,
        };
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
