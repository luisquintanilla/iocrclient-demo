using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using SharedDocumentAnnotation = Microsoft.Extensions.Documents.DocumentAnnotation;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by Azure AI Content Understanding.
/// </summary>
public sealed class ContentUnderstandingClient : IDocumentExtractionClient
{
    private const string ProviderName = "azure-content-understanding";
    private const string PageBreakMarker = "<!-- PageBreak -->";

    public const string DefaultAnalyzerId = "prebuilt-document";

    private readonly Azure.AI.ContentUnderstanding.ContentUnderstandingClient _client;
    private readonly string _analyzerId;

    public ContentUnderstandingClient(
        Uri endpoint,
        TokenCredential credential,
        string analyzerId = DefaultAnalyzerId,
        ContentUnderstandingClientOptions? clientOptions = null)
    {
        _client = new Azure.AI.ContentUnderstanding.ContentUnderstandingClient(
            endpoint,
            credential,
            clientOptions ?? new ContentUnderstandingClientOptions());
        _analyzerId = analyzerId;
    }

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamPages(
            ct => ExtractPagesCoreAsync(document, identifier, mediaType, options, ct),
            cancellationToken);

    private async Task<IReadOnlyList<DocumentExtractionPage>> ExtractPagesCoreAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options,
        CancellationToken cancellationToken)
    {
        DocumentExtractionOptions normalizedOptions = ProviderAdapterHelpers.NormalizeOptions(options);
        ProviderAdapterHelpers.RejectUnsupportedProperties(
            normalizedOptions,
            nameof(ContentUnderstandingClient),
            "processingLocation");
        string analyzerId = ProviderAdapterHelpers.ResolveModel(
            normalizedOptions,
            _analyzerId,
            nameof(ContentUnderstandingClient));
        ProcessingLocation? processingLocation = ParseProcessingLocation(
            ProviderAdapterHelpers.GetProperty<string>(normalizedOptions, "processingLocation"));

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        Operation<AnalysisResult> operation = await _client
            .AnalyzeBinaryAsync(
                WaitUntil.Completed,
                analyzerId,
                BinaryData.FromBytes(buffer.ToArray()),
                contentRange: null,
                contentType: mediaType,
                processingLocation,
                cancellationToken)
            .ConfigureAwait(false);
        AnalysisResult result = operation.Value;
        DocumentContent[] documentContents = result.Contents
            .OfType<DocumentContent>()
            .ToArray();
        if (documentContents.Length == 0
            || documentContents.All(content => content.Pages is not { Count: > 0 }))
        {
            throw new InvalidDataException(
                "Content Understanding returned no document source pages. " +
                "The adapter will not fabricate a page.");
        }

        DocumentSourceOutput sourceOutput = ProviderAdapterHelpers.CreateSourceOutput(
            identifier,
            ProviderName,
            analyzerId,
            normalizedOptions,
            result,
            ProviderAdapterHelpers.GetBufferedResponseBytes(operation.GetRawResponse()));
        var frames = new Dictionary<int, PageFrame>();
        foreach (DocumentContent content in documentContents)
        {
            DocumentCoordinateUnit? unit = MapUnit(content.Unit);
            for (int pageIndex = 0; pageIndex < content.Pages.Count; pageIndex++)
            {
                Azure.AI.ContentUnderstanding.DocumentPage page = content.Pages[pageIndex];
                if (frames.ContainsKey(page.PageNumber))
                {
                    throw new InvalidDataException(
                        $"Content Understanding returned duplicate page number {page.PageNumber}.");
                }

                (DocumentSourcePage sourcePage, DocumentPageReference reference) =
                    ProviderAdapterHelpers.CreatePageFrame(
                        identifier,
                        ProviderName,
                        page.PageNumber,
                        page.Width,
                        page.Height,
                        unit,
                        DocumentCoordinateOrigin.TopLeft,
                        sourceOutput);
                frames.Add(
                    page.PageNumber,
                    new PageFrame(sourcePage, reference, content, page, pageIndex));
            }
        }

        var observationsByPage = frames.Keys.ToDictionary(
            pageNumber => pageNumber,
            _ => new List<DocumentExtractionObservation>());
        int contentIndex = 0;
        foreach (DocumentContent content in documentContents)
        {
            MapParagraphs(content, contentIndex, frames, observationsByPage, sourceOutput);
            MapTables(content, contentIndex, frames, observationsByPage, sourceOutput);
            MapFigures(content, contentIndex, frames, observationsByPage, sourceOutput);
            contentIndex++;
        }

        return frames.Values
            .OrderBy(frame => frame.Page.PageNumber)
            .Select(frame =>
            {
                var annotations = new List<SharedDocumentAnnotation>();
                if (frame.Page.Angle is float angle)
                {
                    annotations.Add(new SharedDocumentAnnotation(
                        "source-page-transform",
                        ProviderAdapterHelpers.StructuredValue(
                            ("rotationClockwiseDegrees", angle),
                            ("basis", "provider-page-top-left")),
                        sourceOutput: sourceOutput,
                        rawRepresentation: frame.Page));
                }

                annotations.AddRange(MapBarcodes(frame.Page, frame.SourcePage, sourceOutput));
                annotations.AddRange(MapFormulas(frame.Page, frame.SourcePage, sourceOutput));
                return new DocumentExtractionPage(
                    frame.Page.PageNumber,
                    observationsByPage[frame.Page.PageNumber],
                    ExtractPageMarkdown(frame.Content, frame.Page, frame.ContentPageIndex),
                    [frame.Reference],
                    frame.SourcePage,
                    annotations,
                    [sourceOutput]);
            })
            .ToArray();
    }

    private static void MapParagraphs(
        DocumentContent content,
        int contentIndex,
        IReadOnlyDictionary<int, PageFrame> frames,
        IDictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        DocumentSourceOutput sourceOutput)
    {
        int paragraphIndex = 0;
        foreach (DocumentParagraph paragraph in content.Paragraphs ?? [])
        {
            IReadOnlyList<DocumentRegion> regions = ParseRegions(paragraph.Source, frames);
            int[] pageNumbers = regions.Select(region => region.Page.PageNumber).Distinct().ToArray();
            foreach (int pageNumber in pageNumbers)
            {
                PageFrame frame = frames[pageNumber];
                DocumentRegion[] pageRegions = regions
                    .Where(region => region.Page.PageNumber == pageNumber)
                    .ToArray();
                var annotation = new SharedDocumentAnnotation(
                    "paragraph",
                    ProviderAdapterHelpers.StructuredValue(
                        ("span", CreateSpanValue(paragraph.Span)),
                        ("source", paragraph.Source)),
                    paragraph.Role?.ToString(),
                    pageRegions,
                    sourceOutput: sourceOutput,
                    rawRepresentation: paragraph);
                string text = paragraph.Content ?? string.Empty;
                observationsByPage[pageNumber].Add(new DocumentExtractionText(
                    $"cu:content:{contentIndex}:paragraph:{paragraphIndex}:{pageNumber}",
                    observationsByPage[pageNumber].Count,
                    ToTextKind(paragraph.Role),
                    text,
                    text,
                    pageReferences: [frame.Reference],
                    annotations: [annotation]));
            }

            paragraphIndex++;
        }
    }

    private static void MapTables(
        DocumentContent content,
        int contentIndex,
        IReadOnlyDictionary<int, PageFrame> frames,
        IDictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        DocumentSourceOutput sourceOutput)
    {
        int tableIndex = 0;
        foreach (Azure.AI.ContentUnderstanding.DocumentTable table in content.Tables ?? [])
        {
            IReadOnlyList<DocumentRegion> tableRegions = ParseRegions(table.Source, frames);
            DocumentPageReference[] pageReferences = tableRegions
                .Select(region => frames[region.Page.PageNumber].Reference)
                .DistinctBy(reference => reference.Value)
                .ToArray();
            var cells = new List<DocumentExtractionTableCell>(table.Cells.Count);
            foreach (Azure.AI.ContentUnderstanding.DocumentTableCell cell in table.Cells)
            {
                IReadOnlyList<DocumentRegion> cellRegions = ParseRegions(cell.Source, frames);
                DocumentPageReference[] cellPageReferences = cellRegions
                    .Select(region => frames[region.Page.PageNumber].Reference)
                    .DistinctBy(reference => reference.Value)
                    .ToArray();
                var annotation = new SharedDocumentAnnotation(
                    "table-cell",
                    ProviderAdapterHelpers.StructuredValue(
                        ("span", CreateSpanValue(cell.Span)),
                        ("source", cell.Source)),
                    cell.Kind?.ToString(),
                    cellRegions,
                    sourceOutput: sourceOutput,
                    rawRepresentation: cell);
                string text = cell.Content ?? string.Empty;
                var cellContent = new DocumentExtractionText(
                    $"cu:content:{contentIndex}:table:{tableIndex}:cell:{cell.RowIndex}:{cell.ColumnIndex}",
                    cell.RowIndex * Math.Max(table.ColumnCount, 1) + cell.ColumnIndex,
                    DocumentTextKind.Paragraph,
                    text,
                    text,
                    pageReferences: cellPageReferences,
                    annotations: [annotation]);
                cells.Add(new DocumentExtractionTableCell(
                    cell.RowIndex,
                    cell.ColumnIndex,
                    cellContent,
                    cell.RowSpan ?? 1,
                    cell.ColumnSpan ?? 1,
                    cell.Kind?.ToString(),
                    [annotation]));
            }

            var tableAnnotation = new SharedDocumentAnnotation(
                "table",
                ProviderAdapterHelpers.StructuredValue(
                    ("span", CreateSpanValue(table.Span)),
                    ("source", table.Source)),
                table.Role?.ToString(),
                tableRegions,
                sourceOutput: sourceOutput,
                rawRepresentation: table);
            string markdown = BuildTableMarkdown(table.RowCount, table.ColumnCount, cells);
            foreach (int pageNumber in tableRegions
                .Select(region => region.Page.PageNumber)
                .Distinct())
            {
                List<DocumentExtractionObservation> observations = observationsByPage[pageNumber];
                observations.Add(new DocumentExtractionTable(
                    $"cu:content:{contentIndex}:table:{tableIndex}",
                    observations.Count,
                    markdown,
                    table.RowCount,
                    table.ColumnCount,
                    cells,
                    pageReferences,
                    annotations: [tableAnnotation]));
            }

            tableIndex++;
        }
    }

    private static void MapFigures(
        DocumentContent content,
        int contentIndex,
        IReadOnlyDictionary<int, PageFrame> frames,
        IDictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        DocumentSourceOutput sourceOutput)
    {
        int figureIndex = 0;
        foreach (DocumentFigure figure in content.Figures ?? [])
        {
            IReadOnlyList<DocumentRegion> regions = ParseRegions(figure.Source, frames);
            DocumentPageReference[] pageReferences = regions
                .Select(region => frames[region.Page.PageNumber].Reference)
                .DistinctBy(reference => reference.Value)
                .ToArray();
            var annotation = new SharedDocumentAnnotation(
                "figure",
                ProviderAdapterHelpers.StructuredValue(
                    ("span", CreateSpanValue(figure.Span)),
                    ("source", figure.Source),
                    ("description", figure.Description)),
                figure.Role?.ToString(),
                regions,
                sourceOutput: sourceOutput,
                rawRepresentation: figure);
            foreach (int pageNumber in regions.Select(region => region.Page.PageNumber).Distinct())
            {
                List<DocumentExtractionObservation> observations = observationsByPage[pageNumber];
                observations.Add(new DocumentExtractionImage(
                    $"cu:content:{contentIndex}:figure:{figureIndex}",
                    observations.Count,
                    "[image]",
                    text: figure.Description,
                    alternativeText: figure.Description,
                    pageReferences: pageReferences,
                    annotations: [annotation]));
            }

            figureIndex++;
        }
    }

    private static IReadOnlyList<DocumentRegion> ParseRegions(
        string? source,
        IReadOnlyDictionary<int, PageFrame> frames)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        var regions = new List<DocumentRegion>();
        foreach (DocumentSource parsed in DocumentSource.Parse(source))
        {
            if (!frames.TryGetValue(parsed.PageNumber, out PageFrame? frame)
                || parsed.Polygon is null)
            {
                continue;
            }

            DocumentRegion? region = ProviderAdapterHelpers.CreateRegion(
                frame.SourcePage,
                parsed.Polygon);
            if (region is not null)
            {
                regions.Add(region);
            }
        }

        return regions;
    }

    private static IEnumerable<SharedDocumentAnnotation> MapBarcodes(
        Azure.AI.ContentUnderstanding.DocumentPage page,
        DocumentSourcePage sourcePage,
        DocumentSourceOutput sourceOutput)
    {
        foreach (DocumentBarcode barcode in page.Barcodes ?? [])
        {
            IReadOnlyList<DocumentRegion> regions = ParseSinglePageRegions(
                barcode.Source,
                sourcePage);
            yield return new SharedDocumentAnnotation(
                "barcode",
                ProviderAdapterHelpers.StructuredValue(
                    ("kind", barcode.Kind.ToString()),
                    ("value", barcode.Value),
                    ("span", CreateSpanValue(barcode.Span)),
                    ("source", barcode.Source)),
                regions: regions,
                confidence: ProviderAdapterHelpers.CreateConfidence(barcode.Confidence),
                sourceOutput: sourceOutput,
                rawRepresentation: barcode);
        }
    }

    private static IEnumerable<SharedDocumentAnnotation> MapFormulas(
        Azure.AI.ContentUnderstanding.DocumentPage page,
        DocumentSourcePage sourcePage,
        DocumentSourceOutput sourceOutput)
    {
        foreach (DocumentFormula formula in page.Formulas ?? [])
        {
            IReadOnlyList<DocumentRegion> regions = ParseSinglePageRegions(
                formula.Source,
                sourcePage);
            yield return new SharedDocumentAnnotation(
                "formula",
                ProviderAdapterHelpers.StructuredValue(
                    ("kind", formula.Kind.ToString()),
                    ("value", formula.Value),
                    ("span", CreateSpanValue(formula.Span)),
                    ("source", formula.Source)),
                regions: regions,
                confidence: ProviderAdapterHelpers.CreateConfidence(formula.Confidence),
                sourceOutput: sourceOutput,
                rawRepresentation: formula);
        }
    }

    private static IReadOnlyList<DocumentRegion> ParseSinglePageRegions(
        string? source,
        DocumentSourcePage sourcePage)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        return DocumentSource.Parse(source)
            .Where(parsed => parsed.PageNumber == sourcePage.PageNumber && parsed.Polygon is not null)
            .Select(parsed => ProviderAdapterHelpers.CreateRegion(sourcePage, parsed.Polygon))
            .Where(region => region is not null)
            .Cast<DocumentRegion>()
            .ToArray();
    }

    private static string ExtractPageMarkdown(
        DocumentContent content,
        Azure.AI.ContentUnderstanding.DocumentPage page,
        int pageIndex)
        => ExtractPageMarkdown(
            content.Markdown,
            page.Spans,
            pageIndex,
            content.Pages.Count,
            content.Pages.Any(candidate => candidate.Spans.Any()));

    internal static string ExtractPageMarkdown(
        string? markdown,
        IEnumerable<ContentSpan> pageSpans,
        int pageIndex,
        int pageCount,
        bool contentHasPageSpans)
    {
        ContentSpan[] spans = pageSpans.ToArray();
        string pageMarkdown = ExtractPageMarkdown(markdown, spans);
        if (spans.Length > 0
            || contentHasPageSpans
            || string.IsNullOrWhiteSpace(markdown))
        {
            return pageMarkdown;
        }

        string[] pages = markdown.Split(
            PageBreakMarker,
            StringSplitOptions.None);
        if (pages.Length != pageCount)
        {
            throw new InvalidDataException(
                "Content Understanding returned document Markdown without page spans " +
                $"or the expected {pageCount - 1} page-break markers.");
        }

        return pages[pageIndex].Trim('\r', '\n');
    }

    internal static string ExtractPageMarkdown(
        string? markdown,
        IEnumerable<ContentSpan> pageSpans)
    {
        ContentSpan[] spans = pageSpans.OrderBy(span => span.Offset).ToArray();
        if (spans.Length > 0 && markdown is not null)
        {
            int first = spans[0].Offset;
            int last = spans.Max(span => span.Offset + span.Length);
            if (first >= 0 && last >= first && last <= markdown.Length)
            {
                return markdown[first..last];
            }
        }

        return string.Empty;
    }

    internal static string BuildTableMarkdown(
        int rowCount,
        int columnCount,
        IReadOnlyList<DocumentExtractionTableCell> cells)
    {
        if (rowCount == 0 || columnCount == 0)
        {
            return "[table]";
        }

        var values = new string[rowCount, columnCount];
        foreach (DocumentExtractionTableCell cell in cells)
        {
            values[cell.Row, cell.Column] = cell.Content?.Markdown ?? string.Empty;
        }

        var rows = new List<string>(rowCount);
        for (int row = 0; row < rowCount; row++)
        {
            var columns = new string[columnCount];
            for (int column = 0; column < columnCount; column++)
            {
                columns[column] = values[row, column] ?? string.Empty;
            }

            rows.Add($"| {string.Join(" | ", columns)} |");
        }

        return string.Join(Environment.NewLine, rows);
    }

    private static ProcessingLocation? ParseProcessingLocation(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? (ProcessingLocation?)null
            : (ProcessingLocation)value;

    private static DocumentCoordinateUnit? MapUnit(Azure.AI.ContentUnderstanding.LengthUnit? unit)
        => unit?.ToString() switch
        {
            "pixel" or "Pixel" => DocumentCoordinateUnit.Pixel,
            "inch" or "Inch" => DocumentCoordinateUnit.Inch,
            null => null,
            string value => throw new NotSupportedException(
                $"Content Understanding coordinate unit '{value}' is not supported."),
        };

    internal static DocumentTextKind ToTextKind(SemanticRole? role)
        => role?.ToString().Contains("heading", StringComparison.OrdinalIgnoreCase) == true
            || role?.ToString().Contains("title", StringComparison.OrdinalIgnoreCase) == true
            ? DocumentTextKind.Header
            : DocumentTextKind.Paragraph;

    internal static object? CreateSpanValue(ContentSpan? span)
        => span is null ? null : new { span.Offset, span.Length };

    private sealed record PageFrame(
        DocumentSourcePage SourcePage,
        DocumentPageReference Reference,
        DocumentContent Content,
        Azure.AI.ContentUnderstanding.DocumentPage Page,
        int ContentPageIndex);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this)
            ? this
            : serviceType == typeof(Azure.AI.ContentUnderstanding.ContentUnderstandingClient)
                ? _client
                : null;

    public void Dispose()
    {
    }
}
