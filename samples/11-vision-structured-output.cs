#:project ocr-shape/OcrShape.csproj
// 11-vision-structured-output.cs — two structured-output patterns on a vision-LLM IOcrClient.
//
// A vision LLM behind IOcrClient normally hands back one blob of freeform Markdown (sample 01). But
// MEAI ships first-class structured output (IChatClient.GetResponseAsync<T> + ForJsonSchema<T>), so a
// vision provider can do better. Two provider-neutral patterns — NEITHER genericizes IOcrClient:
//
//   (A) Structured TRANSCRIPTION (opt-in, inside the client). Ask the model for OcrResult-SHAPED JSON
//       instead of prose, so tables / figure captions / language / confidence come back reliably rather
//       than parsed out of markdown. Opt in with OcrOptions.AdditionalProperties["vision.structured"].
//       Degrades to the freeform path when the model can't honor a schema. Figures are caption-only
//       (OcrImage.Content stays null) — the VLM archetype the nullable Content shape was designed for.
//
//   (B) User-defined typed EXTRACTION (composition, outside the client). For an arbitrary POCO, DON'T
//       grow IOcrClient — reach the inner IChatClient via GetService<IChatClient>() and call
//       GetResponseAsync<T>() yourself. The "OCR-then-extract" pipeline: transcribe, then extract.
//
//   az login
//   dotnet user-secrets set "OCR:OpenAIEndpoint" https://<account>.openai.azure.com --id iocrclient-demo
//   dotnet user-secrets set "OCR:VisionDeployment" gpt-4.1-mini --id iocrclient-demo
//   dotnet run 11-vision-structured-output.cs        # USGS default; pass -- data/survival-kit.pdf for the baseline
using System.ComponentModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using DemoOcr;

string endpoint = Require("OCR:OpenAIEndpoint");
string deployment = DemoOcr.DemoConfig.Config["OCR:VisionDeployment"] ?? "gpt-4.1-mini";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string mediaType = pdf.EndsWith(".pdf") ? "application/pdf"
    : pdf.EndsWith(".jpg") || pdf.EndsWith(".jpeg") ? "image/jpeg" : "image/png";

IChatClient chat = new AzureOpenAIClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

using IOcrClient ocr = new VisionLlmOcrClient(chat);

// --- Pattern A: freeform vs structured transcription, same client, same contract -----------------
Console.WriteLine("=== (A) structured transcription: OFF vs ON ===\n");

OcrResult freeform = await OcrOnce(ocr, pdf, mediaType, structured: false);
Console.WriteLine($"[structured=OFF] source={freeform.OcrSource} pages={freeform.Pages.Count} " +
    $"tables={freeform.Pages.Sum(p => p.Tables.Count)} figures={freeform.Pages.Sum(p => p.Images.Count)} " +
    $"lang={Lang(freeform)} conf={Conf(freeform)}");

OcrResult structured = await OcrOnce(ocr, pdf, mediaType, structured: true);
Console.WriteLine($"[structured=ON ] source={structured.OcrSource} pages={structured.Pages.Count} " +
    $"tables={structured.Pages.Sum(p => p.Tables.Count)} figures={structured.Pages.Sum(p => p.Images.Count)} " +
    $"lang={Lang(structured)} conf={Conf(structured)}");

OcrPage first = structured.Pages[0];
if (first.Tables.Count > 0)
{
    Console.WriteLine($"\n  first table ({first.Tables[0].RowCount}x{first.Tables[0].ColumnCount}):");
    Console.WriteLine("  " + (first.Tables[0].MarkdownRepresentation ?? "(cells only)").Replace("\n", "\n  "));
}
foreach (OcrImage img in first.Images)
{
    Console.WriteLine($"  figure caption (no bytes — VLM archetype): {img.Caption}");
}

// --- Pattern B: OCR-then-extract a typed POCO via the INNER IChatClient (composition) -------------
Console.WriteLine("\n=== (B) OCR-then-extract a typed POCO (GetService<IChatClient>) ===\n");

IChatClient? inner = ocr.GetService(typeof(IChatClient)) as IChatClient;
if (inner is null)
{
    Console.WriteLine("  provider does not expose an inner IChatClient; skipping extraction.");
    return 0;
}

string transcript = string.Join("\n\n", structured.Pages.Select(p => p.Markdown));
ChatResponse<DocumentSummary> extracted = await inner.GetResponseAsync<DocumentSummary>(
    [new ChatMessage(ChatRole.User, $"Extract a structured summary from this document text:\n\n{transcript}")],
    VisionLlmOcrClient.SchemaJson);

if (extracted.TryGetResult(out DocumentSummary? summary))
{
    Console.WriteLine($"  title    : {summary.Title}");
    Console.WriteLine($"  topic    : {summary.Topic}");
    Console.WriteLine($"  keyPoints: {string.Join("; ", summary.KeyPoints ?? [])}");
}
else
{
    Console.WriteLine("  model did not return a parseable DocumentSummary.");
}
return 0;

static async Task<OcrResult> OcrOnce(IOcrClient ocr, string path, string mediaType, bool structured)
{
    OcrOptions? options = structured
        ? new OcrOptions { AdditionalProperties = new() { [VisionLlmOcrClient.StructuredKey] = true } }
        : null;
    await using FileStream doc = File.OpenRead(path);
    return await ocr.ExtractAsync(doc, mediaType, options);
}

static string Lang(OcrResult r) =>
    r.Pages.Count > 0 && r.Pages[0].AdditionalProperties?.TryGetValue("language", out object? l) == true
        ? l?.ToString() ?? "?" : "?";

static string Conf(OcrResult r) =>
    r.Pages.Count > 0 && r.Pages[0].Confidence is double c ? c.ToString("0.00") : "?";

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} (see the header comment).");

// The arbitrary POCO for Pattern B — nothing OCR-specific; the user owns this schema.
sealed class DocumentSummary
{
    [Description("A concise document title.")]
    public string? Title { get; set; }

    [Description("The main topic in a few words.")]
    public string? Topic { get; set; }

    [Description("3-5 key points from the document.")]
    public string[]? KeyPoints { get; set; }
}
