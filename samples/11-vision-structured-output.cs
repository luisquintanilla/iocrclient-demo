#:project ocr-shape/OcrShape.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 11-vision-structured-output.cs — two structured-output patterns on a vision-LLM IDocumentExtractionClient.
//
// A vision LLM behind IDocumentExtractionClient normally hands back one blob of freeform Markdown (sample 01). But
// MEAI ships first-class structured output (IChatClient.GetResponseAsync<T> + ForJsonSchema<T>), so a
// vision provider can ask for a schema. Two provider-neutral patterns follow; neither genericizes
// IDocumentExtractionClient:
//
//   (A) Structured TRANSCRIPTION (opt-in, inside the client). Ask for exact per-page Markdown plus
//       language/confidence. Separate tables or figures without ordering offsets are rejected, and
//       the client falls back to freeform exact Markdown rather than inventing reading order.
//
//   (B) User-defined typed EXTRACTION (composition, outside the client). For an arbitrary POCO, DON'T
//       grow IDocumentExtractionClient — reach the inner IChatClient via GetService<IChatClient>() and call
//       GetResponseAsync<T>() yourself. The "OCR-then-extract" pipeline: transcribe, then extract.
//
//   az login
//   dotnet user-secrets set "OCR:OpenAIEndpoint" https://<account>.openai.azure.com --id iocrclient-demo
//   dotnet user-secrets set "OCR:VisionDeployment" gpt-4.1-mini --id iocrclient-demo
//   dotnet run 11-vision-structured-output.cs        # USGS default; pass -- data/survival-kit.pdf for the baseline
using System.ComponentModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using DemoOcr;

string endpoint = Require("OCR:OpenAIEndpoint");
string deployment = DemoOcr.DemoConfig.Config["OCR:VisionDeployment"] ?? "gpt-4.1-mini";
string pdf = args.Length > 0 ? args[0] : "data/usgs-petroleum-assessment.pdf";
string mediaType = pdf.EndsWith(".pdf") ? "application/pdf"
    : pdf.EndsWith(".jpg") || pdf.EndsWith(".jpeg") ? "image/jpeg" : "image/png";

IChatClient chat = new AzureOpenAIClient(new Uri(endpoint), new Azure.Identity.DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

using IDocumentExtractionClient ocr = new VisionLlmOcrClient(chat);

// --- Pattern A: freeform vs structured transcription, same client, same contract -----------------
Console.WriteLine("=== (A) structured transcription: OFF vs ON ===\n");

DocumentExtractionResult freeform = await OcrOnce(ocr, pdf, mediaType, structured: false);
Console.WriteLine($"[structured=OFF] model={freeform.GetModelId()} pages={freeform.Pages.Count} " +
    $"lang={Lang(freeform)} conf={Conf(freeform)}");

DocumentExtractionResult structured = await OcrOnce(ocr, pdf, mediaType, structured: true);
Console.WriteLine($"[structured=ON ] model={structured.GetModelId()} pages={structured.Pages.Count} " +
    $"lang={Lang(structured)} conf={Conf(structured)}");

// --- Pattern B: OCR-then-extract a typed POCO via the INNER IChatClient (composition) -------------
Console.WriteLine("\n=== (B) OCR-then-extract a typed POCO (GetService<IChatClient>) ===\n");

IChatClient? inner = ocr.GetService(typeof(IChatClient)) as IChatClient;
if (inner is null)
{
    Console.WriteLine("  provider does not expose an inner IChatClient; skipping extraction.");
    return 0;
}

// Typed extraction consumes the deterministic canonical-tree projection, not provider Markdown.
string canonicalTranscript = structured.Text;
ChatResponse<DocumentSummary> extracted = await inner.GetResponseAsync<DocumentSummary>(
    [new ChatMessage(ChatRole.User, $"Extract a structured summary from this document text:\n\n{canonicalTranscript}")],
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

static async Task<DocumentExtractionResult> OcrOnce(IDocumentExtractionClient ocr, string path, string mediaType, bool structured)
{
    DocumentExtractionOptions? options = structured
        ? new DocumentExtractionOptions { AdditionalProperties = new() { [VisionLlmOcrClient.StructuredKey] = true } }
        : null;
    await using FileStream doc = File.OpenRead(path);
    return await ocr.ExtractAsync(doc, mediaType, options);
}

static string Lang(DocumentExtractionResult r) =>
    r.Document.Nodes.OfType<DocumentText>().Select(text => text.Language).FirstOrDefault(language => language is not null) ?? "?";

static string Conf(DocumentExtractionResult r) =>
    r.Pages.SelectMany(page => page.Evidence).Select(evidence => evidence.Confidence).FirstOrDefault() is { } confidence
        ? confidence.ToString("0.00")
        : "?";

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
