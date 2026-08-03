using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;

namespace DemoOcr;

/// <summary>
/// An IDocumentExtractionClient backed by ANY IChatClient vision LLM (gpt-4o, Gemini, Qwen-VL, …) — the Docling
/// ApiVlmModel analog. This is the clean replacement for PdfReadingMode.VisionOnly: "use a vision LLM
/// to transcribe" becomes a swappable PROVIDER behind the OCR contract, not a flag on the reader.
///
/// Insight 3, role 1/3: a vision LLM CAN transcribe, but it is the LOWEST-fidelity option (no native
/// tables/bbox/confidence, nondeterministic, token-expensive). It belongs behind IDocumentExtractionClient as the
/// hybrid fallback — NOT as a first-class document reader, and NOT confused with the vision LLM's real
/// value, which is *understanding* (captioning/field-extraction) via an enricher over IChatClient.
///
/// Round-2 spike (current-repo, not #7588): when the model supports structured output, this client can
/// ask for DocumentExtractionResult-SHAPED JSON instead of freeform markdown, so it fills tables + figure captions +
/// language + confidence reliably rather than by parsing prose. Opt in via
/// <c>DocumentExtractionOptions.AdditionalProperties["vision.structured"] = true</c>; it degrades to the freeform path
/// if the model can't honor a schema. The vision LLM cannot emit image BYTES, so figures are
/// caption-only (DocumentImage.Content stays null) — exactly the archetype the nullable Content shape serves.
/// For arbitrary typed extraction, reach the inner client via <c>GetService&lt;IChatClient&gt;()</c>.
/// </summary>
public sealed class VisionLlmOcrClient(IChatClient chatClient, string? prompt = null) : IDocumentExtractionClient
{
    /// <summary>Opt-in key: set <c>DocumentExtractionOptions.AdditionalProperties["vision.structured"] = true</c> to request structured output.</summary>
    public const string StructuredKey = "vision.structured";

    // Reflection-based resolver so arbitrary DTOs (like VisionDocument) get a schema without source-gen.
    public static readonly JsonSerializerOptions SchemaJson =
        new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private const string DefaultPrompt =
        "Transcribe this page to GitHub-flavored Markdown. Preserve headings, lists, and tables. " +
        "Output only the Markdown, no commentary.";

    private const string StructuredPrompt =
        "Transcribe this document as structured data. For each page provide: the zero-based index; " +
        "GitHub-flavored markdown preserving headings, lists, and tables; a list of tables with " +
        "rowCount, columnCount, and a markdown rendering; a list of figures each with a short caption " +
        "describing the image or chart; the detected language; and a confidence in [0,1].";

    public async Task<DocumentExtractionResult> ExtractAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        byte[] bytes = ms.ToArray();

        bool wantStructured = options?.AdditionalProperties is { } props
            && props.TryGetValue(StructuredKey, out object? v) && v is true;

        if (wantStructured)
        {
            DocumentExtractionResult? structured = await TryStructuredAsync(bytes, mediaType, cancellationToken).ConfigureAwait(false);
            if (structured is not null)
            {
                return structured;
            }
            // Model couldn't honor the schema — fall through to freeform transcription.
        }

        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt ?? DefaultPrompt),
            new DataContent(bytes, mediaType),
        ]);

        ChatResponse response = await chatClient
            .GetResponseAsync(message, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var page = new DocumentPage(1, response.Text);
        return new DocumentExtractionResult([page])
        {
            RawRepresentation = response,
            AdditionalProperties = new() { ["modelId"] = response.ModelId },
        };
    }

    private async Task<DocumentExtractionResult?> TryStructuredAsync(byte[] bytes, string mediaType, CancellationToken cancellationToken)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(StructuredPrompt),
            new DataContent(bytes, mediaType),
        ]);

        try
        {
            ChatResponse<VisionDocument> response = await chatClient
                .GetResponseAsync<VisionDocument>([message], SchemaJson, useJsonSchemaResponseFormat: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.TryGetResult(out VisionDocument? parsed) || parsed?.Pages is not { Count: > 0 })
            {
                return null;
            }

            var pages = new List<DocumentPage>(parsed.Pages.Count);
            foreach (VisionPage vp in parsed.Pages)
            {
                var tables = (vp.Tables ?? []).Select(t =>
                    new DocumentTable(t.RowCount, t.ColumnCount, markdownRepresentation: t.Markdown)).ToList();

                // The VLM archetype: figures are CAPTION-ONLY (no bytes) — DocumentImage.Content stays null.
                var images = (vp.Figures ?? [])
                    .Where(f => !string.IsNullOrWhiteSpace(f.Caption))
                    .Select(f => new DocumentImage { Caption = f.Caption }).ToList();

                pages.Add(new DocumentPage(vp.Index + 1, vp.Markdown ?? "")
                {
                    Elements = tables.Cast<DocumentElement>().Concat(images).ToList(),
                    AdditionalProperties = BuildPageProperties(vp.Language, vp.Confidence),
                });
            }

            return new DocumentExtractionResult(pages)
            {
                RawRepresentation = response,
                AdditionalProperties = new() { ["modelId"] = response.ModelId },
            };
        }
        catch (Exception)
        {
            // Model or endpoint rejected the schema request; caller falls back to freeform.
            return null;
        }
    }

    private static AdditionalPropertiesDictionary? BuildPageProperties(string? language, double? confidence)
    {
        AdditionalPropertiesDictionary? properties = null;
        if (language is { Length: > 0 })
        {
            properties = new() { ["language"] = language };
        }
        if (confidence is { } c)
        {
            properties ??= new();
            properties["confidence"] = c;
        }
        return properties;
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : chatClient.GetService(serviceType, serviceKey);

    public void Dispose() => chatClient.Dispose();

    // DocumentExtractionResult-shaped DTO the vision model is asked to return.
    private sealed class VisionDocument
    {
        [JsonPropertyName("pages")]
        public List<VisionPage>? Pages { get; set; }
    }

    private sealed class VisionPage
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("markdown")] public string? Markdown { get; set; }
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
        [JsonPropertyName("tables")] public List<VisionTable>? Tables { get; set; }
        [JsonPropertyName("figures")] public List<VisionFigure>? Figures { get; set; }
    }

    private sealed class VisionTable
    {
        [JsonPropertyName("rowCount")] public int RowCount { get; set; }
        [JsonPropertyName("columnCount")] public int ColumnCount { get; set; }
        [JsonPropertyName("markdown")] public string? Markdown { get; set; }
    }

    private sealed class VisionFigure
    {
        [JsonPropertyName("caption")] public string? Caption { get; set; }
    }
}
