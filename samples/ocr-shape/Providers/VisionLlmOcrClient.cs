using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by an <see cref="IChatClient"/> vision model.
/// </summary>
public sealed class VisionLlmOcrClient(
    IChatClient chatClient,
    string? prompt = null,
    bool useStructuredOutput = false,
    ILogger<VisionLlmOcrClient>? logger = null,
    PdfPageRasterizer? pdfRasterizer = null) : IDocumentExtractionClient
{
    private const string ProviderName = "vision-llm";
    private readonly PdfPageRasterizer _pdfRasterizer =
        pdfRasterizer ?? new PdfPageRasterizer();

    public static readonly EventId StructuredOutputFallbackEventId =
        new(1, "StructuredOutputFallback");

    public const string StructuredKey = "vision.structured";

    public static readonly JsonSerializerOptions SchemaJson =
        new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private const string DefaultPrompt =
        "Transcribe this image to GitHub-flavored Markdown. Preserve headings, lists, and tables. " +
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
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractPagesCoreAsync(document, identifier, mediaType, options, cancellationToken);

    private async IAsyncEnumerable<DocumentExtractionPage> ExtractPagesCoreAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DocumentExtractionOptions normalizedOptions = ProviderAdapterHelpers.NormalizeOptions(options);
        ProviderAdapterHelpers.RejectUnsupportedProperties(
            normalizedOptions,
            nameof(VisionLlmOcrClient));
        var chatOptions = new ChatOptions { ModelId = normalizedOptions.ModelId };

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        byte[] bytes = buffer.ToArray();

        Exception? fallbackReason = null;
        if (useStructuredOutput)
        {
            IReadOnlyList<DocumentExtractionPage>? structuredPages = null;
            try
            {
                structuredPages = await ExtractStructuredPagesAsync(
                    bytes,
                    identifier,
                    mediaType,
                    normalizedOptions,
                    chatOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (CanFallbackToFreeform(exception))
            {
                fallbackReason = exception;
                logger?.LogWarning(
                    StructuredOutputFallbackEventId,
                    exception,
                    "Vision structured output is unavailable for media type {MediaType}; " +
                    "falling back to free-form extraction.",
                    mediaType);
            }

            if (structuredPages is not null)
            {
                foreach (DocumentExtractionPage page in structuredPages)
                {
                    yield return page;
                }

                yield break;
            }
        }

        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            using PdfDocument pdf = PdfDocument.Open(new MemoryStream(bytes, writable: false));
            for (int pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Page page = pdf.GetPage(pageNumber);
                PdfRasterizedPage rendered = _pdfRasterizer.RenderPage(
                    bytes,
                    pageNumber,
                    page.Width,
                    page.Height,
                    page.Rotation.Value,
                    page.CropBox.Bounds.Left,
                    page.CropBox.Bounds.Bottom,
                    page.CropBox.Bounds.Right,
                    page.CropBox.Bounds.Top);
                yield return await ExtractFreeformPageAsync(
                    rendered.PngBytes,
                    "image/png",
                    identifier,
                    pageNumber,
                    page.Width,
                    page.Height,
                    DocumentCoordinateUnit.Point,
                    DocumentCoordinateOrigin.BottomLeft,
                    rendered.Transform,
                    fallbackReason,
                    normalizedOptions,
                    chatOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            yield break;
        }

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "Free-form vision extraction supports image/* and application/pdf inputs.");
        }

        yield return await ExtractFreeformPageAsync(
            bytes,
            mediaType,
            identifier,
            1,
            null,
            null,
            null,
            null,
            null,
            fallbackReason,
            normalizedOptions,
            chatOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<DocumentExtractionPage> ExtractFreeformPageAsync(
        byte[] bytes,
        string mediaType,
        string identifier,
        int pageNumber,
        double? width,
        double? height,
        DocumentCoordinateUnit? unit,
        DocumentCoordinateOrigin? origin,
        PdfRasterTransform? rasterTransform,
        Exception? fallbackReason,
        DocumentExtractionOptions normalizedOptions,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(prompt ?? DefaultPrompt),
            new DataContent(bytes, mediaType),
        ]);
        ChatResponse response = await chatClient
            .GetResponseAsync(message, chatOptions, cancellationToken)
            .ConfigureAwait(false);
        DocumentSourceOutput sourceOutput = ProviderAdapterHelpers.CreateSourceOutput(
            $"{identifier}:page:{pageNumber}",
            ProviderName,
            normalizedOptions.ModelId ?? response.ModelId ?? "unspecified",
            normalizedOptions,
            response.RawRepresentation);
        (DocumentSourcePage sourcePage, DocumentPageReference pageReference) =
            ProviderAdapterHelpers.CreatePageFrame(
                identifier,
                ProviderName,
                pageNumber,
                width,
                height,
                unit,
                origin,
                sourceOutput);
        var pageAnnotations = new List<DocumentAnnotation>();
        if (rasterTransform is not null)
        {
            pageAnnotations.Add(new DocumentAnnotation(
                "pdf-raster-transform",
                rasterTransform,
                role: "source-page-transform",
                sourceOutput: sourceOutput));
        }

        if (fallbackReason is not null)
        {
            pageAnnotations.Add(new DocumentAnnotation(
                "structured-output-fallback",
                ProviderAdapterHelpers.StructuredValue(
                    ("reason", "provider-capability"),
                    ("exceptionType", fallbackReason.GetType().FullName),
                    ("structuredOutputRequested", true)),
                sourceOutput: sourceOutput,
                rawRepresentation: response.RawRepresentation));
        }

        string markdown = response.Text;
        return new DocumentExtractionPage(
            pageNumber,
            string.IsNullOrWhiteSpace(markdown)
                ? []
                :
                [
                    new DocumentExtractionText(
                        $"vision:{pageNumber}:text",
                        0,
                        DocumentTextKind.Paragraph,
                        markdown,
                        markdown,
                        pageReferences: [pageReference],
                        annotations:
                        [
                            new DocumentAnnotation(
                                "text",
                                sourceOutput: sourceOutput,
                                rawRepresentation: response.RawRepresentation),
                        ]),
                ],
            markdown,
            [pageReference],
            sourcePage,
            annotations: pageAnnotations,
            sourceOutputs: [sourceOutput]);
    }

    private async Task<IReadOnlyList<DocumentExtractionPage>> ExtractStructuredPagesAsync(
        byte[] bytes,
        string identifier,
        string mediaType,
        DocumentExtractionOptions normalizedOptions,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var message = new ChatMessage(ChatRole.User,
        [
            new TextContent(StructuredPrompt),
            new DataContent(bytes, mediaType),
        ]);
        ChatResponse<VisionDocument> response = await chatClient
            .GetResponseAsync<VisionDocument>(
                [message],
                SchemaJson,
                chatOptions,
                useJsonSchemaResponseFormat: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!response.TryGetResult(out VisionDocument? parsed)
            || parsed?.Pages is not { Count: > 0 })
        {
            throw new InvalidDataException(
                "The vision model did not return the required structured page array.");
        }

        DocumentSourceOutput sourceOutput = ProviderAdapterHelpers.CreateSourceOutput(
            identifier,
            ProviderName,
            normalizedOptions.ModelId ?? response.ModelId ?? "unspecified",
            normalizedOptions,
            response.RawRepresentation);
        var pages = new List<DocumentExtractionPage>(parsed.Pages.Count);
        foreach (VisionPage page in parsed.Pages.OrderBy(page => page.Index))
        {
            int pageNumber = checked(page.Index + 1);
            (DocumentSourcePage sourcePage, DocumentPageReference pageReference) =
                ProviderAdapterHelpers.CreatePageFrame(
                    identifier,
                    ProviderName,
                    pageNumber,
                    null,
                    null,
                    null,
                    null,
                    sourceOutput);
            var observations = new List<DocumentExtractionObservation>();
            var pageAnnotations = new List<DocumentAnnotation>();
            if (page.Confidence is double confidence)
            {
                if (confidence is < 0 or > 1)
                {
                    throw new InvalidDataException(
                        $"Vision page {page.Index} confidence '{confidence}' is outside [0,1].");
                }

                pageAnnotations.Add(new DocumentAnnotation(
                    "vision-page",
                    ProviderAdapterHelpers.StructuredValue(("language", page.Language)),
                    confidence: new DocumentConfidence(confidence, "model-self-reported"),
                    sourceOutput: sourceOutput,
                    rawRepresentation: response.RawRepresentation));
            }
            else if (!string.IsNullOrWhiteSpace(page.Language))
            {
                pageAnnotations.Add(new DocumentAnnotation(
                    "vision-page",
                    ProviderAdapterHelpers.StructuredValue(("language", page.Language)),
                    sourceOutput: sourceOutput,
                    rawRepresentation: response.RawRepresentation));
            }

            if (!string.IsNullOrWhiteSpace(page.Markdown))
            {
                observations.Add(new DocumentExtractionText(
                    $"vision:{page.Index}:text",
                    observations.Count,
                    DocumentTextKind.Paragraph,
                    page.Markdown,
                    page.Markdown,
                    pageReferences: [pageReference],
                    annotations:
                    [
                        new DocumentAnnotation(
                            "text",
                            sourceOutput: sourceOutput,
                            rawRepresentation: response.RawRepresentation),
                    ]));
            }

            foreach (VisionTable table in page.Tables ?? [])
            {
                string markdown = string.IsNullOrWhiteSpace(table.Markdown)
                    ? "[table]"
                    : table.Markdown;
                observations.Add(new DocumentExtractionTable(
                    $"vision:{page.Index}:table:{observations.Count}",
                    observations.Count,
                    markdown,
                    table.RowCount,
                    table.ColumnCount,
                    [],
                    [pageReference],
                    annotations:
                    [
                        new DocumentAnnotation(
                            "table",
                            ProviderAdapterHelpers.StructuredValue(
                                ("cellStatus", "unavailable-from-provider")),
                            sourceOutput: sourceOutput,
                            rawRepresentation: response.RawRepresentation),
                    ]));
            }

            foreach (VisionFigure figure in page.Figures ?? [])
            {
                if (!string.IsNullOrWhiteSpace(figure.Caption))
                {
                    observations.Add(new DocumentExtractionImage(
                        $"vision:{page.Index}:image:{observations.Count}",
                        observations.Count,
                        "[image]",
                        text: figure.Caption,
                        alternativeText: figure.Caption,
                        pageReferences: [pageReference],
                        annotations:
                        [
                            new DocumentAnnotation(
                                "figure",
                                ProviderAdapterHelpers.StructuredValue(("caption", figure.Caption)),
                                sourceOutput: sourceOutput,
                                rawRepresentation: response.RawRepresentation),
                        ]));
                }
            }

            pages.Add(new DocumentExtractionPage(
                pageNumber,
                observations,
                page.Markdown,
                [pageReference],
                sourcePage,
                pageAnnotations,
                [sourceOutput]));
        }

        return pages;
    }

    public static bool CanFallbackToFreeform(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException
            or Azure.Identity.AuthenticationFailedException
            or Azure.RequestFailedException
            or HttpRequestException
            or TimeoutException)
        {
            return false;
        }

        return exception is (NotSupportedException or InvalidOperationException)
            && (exception.Message.Contains("schema", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("response format", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("structured output", StringComparison.OrdinalIgnoreCase));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this)
            ? this
            : chatClient.GetService(serviceType, serviceKey);

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
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("confidence")] public double? Confidence { get; set; }
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
