using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Azure;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by an <see cref="IChatClient"/> vision model.
/// </summary>
public sealed class VisionLlmOcrClient(
    IChatClient chatClient,
    string? prompt = null,
    bool useStructuredOutput = false) : IDocumentExtractionClient
{
    public const string StructuredKey = "vision.structured";

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

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        CancellationToken cancellationToken = default)
        => ExtractPagesCoreAsync(document, mediaType, cancellationToken);

    private async IAsyncEnumerable<DocumentExtractionPage> ExtractPagesCoreAsync(
        Stream document,
        string mediaType,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        byte[] bytes = ms.ToArray();

        if (useStructuredOutput
            && await TryStructuredPagesAsync(bytes, mediaType, cancellationToken).ConfigureAwait(false)
                is { } structuredPages)
        {
            foreach (DocumentExtractionPage page in structuredPages)
            {
                yield return page;
            }

            yield break;
        }

        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt ?? DefaultPrompt),
            new DataContent(bytes, mediaType),
        ]);

        ChatResponse response = await chatClient
            .GetResponseAsync(message, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        string markdown = response.Text;
        yield return new DocumentExtractionPage(
            1,
            string.IsNullOrWhiteSpace(markdown)
                ? []
                : [new DocumentExtractionText("vision:1:text", 0, DocumentTextKind.Paragraph, markdown, markdown)],
            markdown,
            [new DocumentPageReference("page:1")]);
    }

    private async Task<IReadOnlyList<DocumentExtractionPage>?> TryStructuredPagesAsync(
        byte[] bytes,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(StructuredPrompt),
            new DataContent(bytes, mediaType),
        ]);

        try
        {
            ChatResponse<VisionDocument> response = await chatClient
                .GetResponseAsync<VisionDocument>(
                    [message],
                    SchemaJson,
                    useJsonSchemaResponseFormat: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!response.TryGetResult(out VisionDocument? parsed) || parsed?.Pages is not { Count: > 0 })
            {
                return null;
            }

            var pages = new List<DocumentExtractionPage>(parsed.Pages.Count);
            foreach (VisionPage page in parsed.Pages)
            {
                var observations = new List<DocumentExtractionObservation>();
                DocumentPageReference pageReference = new($"page:{page.Index + 1}");
                if (!string.IsNullOrWhiteSpace(page.Markdown))
                {
                    observations.Add(new DocumentExtractionText(
                        $"vision:{page.Index}:text",
                        0,
                        DocumentTextKind.Paragraph,
                        page.Markdown,
                        page.Markdown,
                        pageReferences: [pageReference]));
                }

                foreach (VisionTable table in page.Tables ?? [])
                {
                    observations.Add(new DocumentExtractionTable(
                        $"vision:{page.Index}:table:{observations.Count}",
                        observations.Count,
                        string.IsNullOrWhiteSpace(table.Markdown) ? "[table]" : table.Markdown,
                        table.RowCount,
                        table.ColumnCount,
                        pageReferences: [pageReference]));
                }

                foreach (VisionFigure figure in page.Figures ?? [])
                {
                    if (string.IsNullOrWhiteSpace(figure.Caption))
                    {
                        continue;
                    }

                    observations.Add(new DocumentExtractionImage(
                        $"vision:{page.Index}:image:{observations.Count}",
                        observations.Count,
                        "[image]",
                        text: figure.Caption,
                        alternativeText: figure.Caption,
                        pageReferences: [pageReference]));
                }

                pages.Add(new DocumentExtractionPage(
                    page.Index + 1,
                    observations,
                    page.Markdown,
                    [pageReference]));
            }

            return pages;
        }
        catch (Exception exception) when (CanFallbackToFreeform(exception))
        {
            // Only an explicit schema/response-format rejection may degrade to freeform.
            return null;
        }
    }

    public static bool CanFallbackToFreeform(Exception exception)
    {
        if (exception is OperationCanceledException
            or AuthenticationFailedException
            or RequestFailedException
            or HttpRequestException
            or TimeoutException)
        {
            return false;
        }

        return exception is NotSupportedException or InvalidOperationException
            && (exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("response format", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("structured output", StringComparison.OrdinalIgnoreCase));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : chatClient.GetService(serviceType, serviceKey);

    public void Dispose() => chatClient.Dispose();

    private sealed class VisionDocument
    {
        [JsonPropertyName("pages")]
        public List<VisionPage>? Pages { get; set; }
    }

    private sealed class VisionPage
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("markdown")] public string? Markdown { get; set; }
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
