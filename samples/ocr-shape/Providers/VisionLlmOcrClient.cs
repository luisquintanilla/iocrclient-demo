using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

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
/// When the model supports structured output, this client can request exact page Markdown plus
/// language and confidence. Opt in via
/// <c>DocumentExtractionOptions.AdditionalProperties["vision.structured"] = true</c>; it degrades to the freeform path
/// if the model cannot honor the schema or returns separate tables/figures without ordering offsets.
/// The client never fabricates a canonical order by subtracting and appending structural fragments.
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
        "Transcribe this document as structured data. For each page provide the zero-based index, " +
        "one exact GitHub-flavored Markdown rendering in reading order, detected language, and confidence in [0,1]. " +
        "Do not return separate table or figure collections because they do not carry ordering offsets.";

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

        string projected = DocumentExtractionDemoExtensions.ProjectProviderMarkdown(response.Text);
        var nodes = new List<DocumentNode>();
        if (projected.Length > 0)
        {
            nodes.Add(DocumentExtractionDemoExtensions.CreateTextNode("vision", 1, 0, projected));
        }

        var page = new DocumentPage(
            1,
            DocumentExtractionDemoExtensions.CreatePageDocument("vision", 1, nodes),
            markdown: response.Text)
        {
            RawRepresentation = response,
        };
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

            if (!HasSequentialPageIndices(parsed.Pages.Select(static page => page.Index)) ||
                parsed.Pages.Any(page => !CanPreserveStructuredOrder(
                page.Markdown,
                page.Tables?.Count ?? 0,
                page.Figures?.Count ?? 0)))
            {
                return null;
            }

            var pages = new List<DocumentPage>(parsed.Pages.Count);
            foreach (VisionPage vp in parsed.Pages)
            {
                int pageNumber = vp.Index + 1;
                string projected = DocumentExtractionDemoExtensions.ProjectProviderMarkdown(vp.Markdown!);
                var nodes = new List<DocumentNode>();
                var evidence = new List<DocumentExtractionEvidence>();
                if (projected.Length > 0)
                {
                    DocumentText textNode = DocumentExtractionDemoExtensions.CreateTextNode(
                        "vision",
                        pageNumber,
                        nodes.Count,
                        projected,
                        language: vp.Language);
                    nodes.Add(textNode);
                    if (vp.Confidence is { } confidence)
                    {
                        evidence.Add(new DocumentExtractionEvidence(textNode.Id) { Confidence = confidence });
                    }
                }

                pages.Add(new DocumentPage(
                    pageNumber,
                    DocumentExtractionDemoExtensions.CreatePageDocument("vision", pageNumber, nodes),
                    markdown: vp.Markdown,
                    evidence: evidence));
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

    public static bool CanPreserveStructuredOrder(
        string? markdown,
        int tableCount,
        int figureCount)
        => !string.IsNullOrWhiteSpace(markdown)
            && tableCount == 0
            && figureCount == 0;

    public static bool HasSequentialPageIndices(IEnumerable<int> indices)
    {
        int expected = 0;
        foreach (int index in indices)
        {
            if (index != expected++)
            {
                return false;
            }
        }
        return expected > 0;
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => DocumentExtractionDemoExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

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
        [JsonPropertyName("markdown")] public string? Markdown { get; set; }
    }

    private sealed class VisionFigure
    {
        [JsonPropertyName("caption")] public string? Caption { get; set; }
    }
}
