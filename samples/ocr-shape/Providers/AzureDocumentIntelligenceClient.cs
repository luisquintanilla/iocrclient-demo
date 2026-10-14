using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by Azure Document Intelligence.
/// </summary>
public sealed class AzureDocumentIntelligenceClient : IDocumentExtractionClient
{
    private const string ProviderName = "azure-document-intelligence";

    private readonly DocumentIntelligenceClient _client;
    private readonly string _defaultModel;
    private readonly bool _includeImages;

    public AzureDocumentIntelligenceClient(
        Uri endpoint,
        TokenCredential credential,
        string defaultModel = "prebuilt-layout",
        bool includeImages = false,
        DocumentIntelligenceClientOptions? clientOptions = null)
    {
        _client = new DocumentIntelligenceClient(
            endpoint,
            credential,
            clientOptions ?? new DocumentIntelligenceClientOptions());
        _defaultModel = defaultModel;
        _includeImages = includeImages;
    }

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamPages(
            ct => ExtractPagesCoreAsync(document, identifier, options, ct),
            cancellationToken);

    private async Task<IReadOnlyList<DocumentExtractionPage>> ExtractPagesCoreAsync(
        Stream document,
        string identifier,
        DocumentExtractionOptions? options,
        CancellationToken cancellationToken)
    {
        DocumentExtractionOptions normalizedOptions = ProviderAdapterHelpers.NormalizeOptions(options);
        ProviderAdapterHelpers.RejectUnsupportedProperties(
            normalizedOptions,
            nameof(AzureDocumentIntelligenceClient),
            "locale",
            "pages",
            "features");
        string model = ProviderAdapterHelpers.ResolveModel(
            normalizedOptions,
            _defaultModel,
            nameof(AzureDocumentIntelligenceClient));

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        var analyzeOptions = new AnalyzeDocumentOptions(model, BinaryData.FromBytes(buffer.ToArray()))
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
            Locale = ProviderAdapterHelpers.GetProperty<string>(normalizedOptions, "locale"),
            Pages = ProviderAdapterHelpers.GetProperty<string>(normalizedOptions, "pages"),
        };
        AddFeatures(
            analyzeOptions,
            ProviderAdapterHelpers.GetProperty<IEnumerable<string>>(normalizedOptions, "features"));
        if (_includeImages)
        {
            analyzeOptions.Output.Add(AnalyzeOutputOption.Figures);
        }

        Operation<AnalyzeResult> operation = await _client
            .AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken)
            .ConfigureAwait(false);
        AnalyzeResult result = operation.Value;
        if (result.Pages is not { Count: > 0 })
        {
            throw new InvalidDataException(
                "Azure Document Intelligence returned no source pages. The adapter will not fabricate a page.");
        }

        DocumentSourceOutput sourceOutput = ProviderAdapterHelpers.CreateSourceOutput(
            identifier,
            ProviderName,
            model,
            normalizedOptions,
            result,
            ProviderAdapterHelpers.GetBufferedResponseBytes(operation.GetRawResponse()));
        IReadOnlyDictionary<int, PageFrame> frames = result.Pages.ToDictionary(
            page => page.PageNumber,
            page =>
            {
                (DocumentSourcePage sourcePage, DocumentPageReference reference) =
                    ProviderAdapterHelpers.CreatePageFrame(
                        identifier,
                        ProviderName,
                        page.PageNumber,
                        page.Width,
                        page.Height,
                        MapUnit(page.Unit),
                        DocumentCoordinateOrigin.TopLeft,
                        sourceOutput);
                return new PageFrame(sourcePage, reference);
            });

        var observationsByPage = result.Pages.ToDictionary(
            page => page.PageNumber,
            _ => new List<DocumentExtractionObservation>());

        MapParagraphs(result, frames, observationsByPage, sourceOutput);
        MapTables(result, frames, observationsByPage, sourceOutput);
        if (_includeImages)
        {
            await MapImagesAsync(
                result,
                operation.Id,
                model,
                frames,
                observationsByPage,
                sourceOutput,
                cancellationToken).ConfigureAwait(false);
        }

        return result.Pages
            .OrderBy(page => page.PageNumber)
            .Select(page =>
            {
                PageFrame frame = frames[page.PageNumber];
                var annotations = new List<DocumentAnnotation>();
                if (page.Angle.HasValue)
                {
                    annotations.Add(new DocumentAnnotation(
                        "source-page-transform",
                        ProviderAdapterHelpers.StructuredValue(
                            ("rotationClockwiseDegrees", page.Angle.Value),
                            ("basis", "provider-page-top-left")),
                        sourceOutput: sourceOutput,
                        rawRepresentation: page));
                }

                annotations.AddRange(MapSelectionMarks(page, frame.SourcePage, sourceOutput));
                annotations.AddRange(MapBarcodes(page, frame.SourcePage, sourceOutput));

                return new DocumentExtractionPage(
                    page.PageNumber,
                    observationsByPage[page.PageNumber],
                    ExtractPageMarkdown(result, page),
                    [frame.Reference],
                    frame.SourcePage,
                    annotations,
                    [sourceOutput]);
            })
            .ToArray();
    }

    private static void MapParagraphs(
        AnalyzeResult result,
        IReadOnlyDictionary<int, PageFrame> frames,
        IDictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        DocumentSourceOutput sourceOutput)
    {
        int paragraphIndex = 0;
        foreach (DocumentParagraph paragraph in result.Paragraphs ?? [])
        {
            string text = paragraph.Content ?? string.Empty;
            foreach (IGrouping<int, BoundingRegion> pageRegions in paragraph.BoundingRegions
                .Where(region => frames.ContainsKey(region.PageNumber))
                .GroupBy(region => region.PageNumber))
            {
                PageFrame frame = frames[pageRegions.Key];
                DocumentRegion[] regions = pageRegions
                    .Select(region => ProviderAdapterHelpers.CreateRegion(frame.SourcePage, region.Polygon))
                    .Where(region => region is not null)
                    .Cast<DocumentRegion>()
                    .ToArray();
                var annotation = new DocumentAnnotation(
                    "paragraph",
                    ProviderAdapterHelpers.StructuredValue(
                        ("spans", paragraph.Spans.Select(span => new { span.Offset, span.Length }).ToArray())),
                    NormalizeRole(paragraph.Role),
                    regions,
                    sourceOutput: sourceOutput,
                    rawRepresentation: paragraph);

                observationsByPage[pageRegions.Key].Add(new DocumentExtractionText(
                    $"di:paragraph:{paragraphIndex}:{pageRegions.Key}",
                    observationsByPage[pageRegions.Key].Count,
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
        AnalyzeResult result,
        IReadOnlyDictionary<int, PageFrame> frames,
        IDictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        DocumentSourceOutput sourceOutput)
    {
        int tableIndex = 0;
        foreach (Azure.AI.DocumentIntelligence.DocumentTable table in result.Tables ?? [])
        {
            DocumentPageReference[] pageReferences = table.BoundingRegions
                .Where(region => frames.ContainsKey(region.PageNumber))
                .Select(region => frames[region.PageNumber].Reference)
                .DistinctBy(reference => reference.Value)
                .ToArray();
            DocumentRegion[] tableRegions = table.BoundingRegions
                .Where(region => frames.ContainsKey(region.PageNumber))
                .Select(region => ProviderAdapterHelpers.CreateRegion(
                    frames[region.PageNumber].SourcePage,
                    region.Polygon))
                .Where(region => region is not null)
                .Cast<DocumentRegion>()
                .ToArray();

            var cells = new List<DocumentExtractionTableCell>(table.Cells.Count);
            foreach (Azure.AI.DocumentIntelligence.DocumentTableCell cell in table.Cells)
            {
                DocumentRegion[] cellRegions = cell.BoundingRegions
                    .Where(region => frames.ContainsKey(region.PageNumber))
                    .Select(region => ProviderAdapterHelpers.CreateRegion(
                        frames[region.PageNumber].SourcePage,
                        region.Polygon))
                    .Where(region => region is not null)
                    .Cast<DocumentRegion>()
                    .ToArray();
                DocumentPageReference[] cellPageReferences = cell.BoundingRegions
                    .Where(region => frames.ContainsKey(region.PageNumber))
                    .Select(region => frames[region.PageNumber].Reference)
                    .DistinctBy(reference => reference.Value)
                    .ToArray();
                string text = cell.Content ?? string.Empty;
                var cellAnnotation = new DocumentAnnotation(
                    "table-cell",
                    ProviderAdapterHelpers.StructuredValue(
                        ("spans", cell.Spans.Select(span => new { span.Offset, span.Length }).ToArray())),
                    cell.Kind.ToString(),
                    cellRegions,
                    sourceOutput: sourceOutput,
                    rawRepresentation: cell);
                var content = new DocumentExtractionText(
                    $"di:table:{tableIndex}:cell:{cell.RowIndex}:{cell.ColumnIndex}",
                    cell.RowIndex * Math.Max(table.ColumnCount, 1) + cell.ColumnIndex,
                    DocumentTextKind.Paragraph,
                    text,
                    text,
                    pageReferences: cellPageReferences,
                    annotations: [cellAnnotation]);
                cells.Add(new DocumentExtractionTableCell(
                    cell.RowIndex,
                    cell.ColumnIndex,
                    content,
                    cell.RowSpan ?? 1,
                    cell.ColumnSpan ?? 1,
                    cell.Kind.ToString(),
                    [cellAnnotation]));
            }

            var tableAnnotation = new DocumentAnnotation(
                "table",
                ProviderAdapterHelpers.StructuredValue(
                    ("spans", table.Spans.Select(span => new { span.Offset, span.Length }).ToArray())),
                regions: tableRegions,
                sourceOutput: sourceOutput,
                rawRepresentation: table);
            string markdown = BuildTableMarkdown(table.RowCount, table.ColumnCount, cells);
            var mappedTable = new DocumentExtractionTable(
                $"di:table:{tableIndex++}",
                0,
                markdown,
                table.RowCount,
                table.ColumnCount,
                cells,
                pageReferences,
                annotations: [tableAnnotation]);

            foreach (int pageNumber in table.BoundingRegions
                .Select(region => region.PageNumber)
                .Where(frames.ContainsKey)
                .Distinct())
            {
                List<DocumentExtractionObservation> observations = observationsByPage[pageNumber];
                observations.Add(new DocumentExtractionTable(
                    mappedTable.ProviderId,
                    observations.Count,
                    mappedTable.Markdown,
                    mappedTable.RowCount,
                    mappedTable.ColumnCount,
                    mappedTable.Cells,
                    mappedTable.PageReferences,
                    annotations: mappedTable.Annotations));
            }
        }
    }

    private async Task MapImagesAsync(
        AnalyzeResult result,
        string operationId,
        string model,
        IReadOnlyDictionary<int, PageFrame> frames,
        IDictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        DocumentSourceOutput sourceOutput,
        CancellationToken cancellationToken)
    {
        int imageIndex = 0;
        foreach (DocumentFigure figure in result.Figures ?? [])
        {
            DocumentPageReference[] pageReferences = figure.BoundingRegions
                .Where(region => frames.ContainsKey(region.PageNumber))
                .Select(region => frames[region.PageNumber].Reference)
                .DistinctBy(reference => reference.Value)
                .ToArray();
            DocumentRegion[] regions = figure.BoundingRegions
                .Where(region => frames.ContainsKey(region.PageNumber))
                .Select(region => ProviderAdapterHelpers.CreateRegion(
                    frames[region.PageNumber].SourcePage,
                    region.Polygon))
                .Where(region => region is not null)
                .Cast<DocumentRegion>()
                .ToArray();
            ReadOnlyMemory<byte>? content = null;
            if (!string.IsNullOrWhiteSpace(figure.Id))
            {
                Response<BinaryData> figureResponse = await _client
                    .GetAnalyzeResultFigureAsync(model, operationId, figure.Id, cancellationToken)
                    .ConfigureAwait(false);
                content = figureResponse.Value.ToMemory().ToArray();
            }

            var annotation = new DocumentAnnotation(
                "figure",
                ProviderAdapterHelpers.StructuredValue(
                    ("spans", figure.Spans.Select(span => new { span.Offset, span.Length }).ToArray())),
                regions: regions,
                sourceOutput: sourceOutput,
                rawRepresentation: figure);
            var image = new DocumentExtractionImage(
                $"di:image:{imageIndex++}",
                0,
                "[image]",
                content: content,
                mediaType: content is null ? null : "image/png",
                alternativeText: figure.Caption?.Content,
                pageReferences: pageReferences,
                annotations: [annotation]);

            foreach (int pageNumber in figure.BoundingRegions
                .Select(region => region.PageNumber)
                .Where(frames.ContainsKey)
                .Distinct())
            {
                List<DocumentExtractionObservation> observations = observationsByPage[pageNumber];
                observations.Add(new DocumentExtractionImage(
                    image.ProviderId,
                    observations.Count,
                    image.Markdown,
                    image.Text,
                    image.Content,
                    image.MediaType,
                    image.Source,
                    image.AlternativeText,
                    image.PageReferences,
                    annotations: image.Annotations));
            }
        }
    }

    private static IEnumerable<DocumentAnnotation> MapSelectionMarks(
        Azure.AI.DocumentIntelligence.DocumentPage page,
        DocumentSourcePage sourcePage,
        DocumentSourceOutput sourceOutput)
    {
        foreach (DocumentSelectionMark mark in page.SelectionMarks ?? [])
        {
            DocumentRegion? region = ProviderAdapterHelpers.CreateRegion(sourcePage, mark.Polygon);
            yield return new DocumentAnnotation(
                "selection-mark",
                ProviderAdapterHelpers.StructuredValue(
                    ("state", mark.State.ToString()),
                    ("span", new { mark.Span.Offset, mark.Span.Length })),
                regions: region is null ? null : [region],
                confidence: new DocumentConfidence(mark.Confidence, "provider"),
                sourceOutput: sourceOutput,
                rawRepresentation: mark);
        }
    }

    private static IEnumerable<DocumentAnnotation> MapBarcodes(
        Azure.AI.DocumentIntelligence.DocumentPage page,
        DocumentSourcePage sourcePage,
        DocumentSourceOutput sourceOutput)
    {
        foreach (DocumentBarcode barcode in page.Barcodes ?? [])
        {
            DocumentRegion? region = ProviderAdapterHelpers.CreateRegion(sourcePage, barcode.Polygon);
            yield return new DocumentAnnotation(
                "barcode",
                ProviderAdapterHelpers.StructuredValue(
                    ("kind", barcode.Kind.ToString()),
                    ("value", barcode.Value),
                    ("span", new { barcode.Span.Offset, barcode.Span.Length })),
                regions: region is null ? null : [region],
                confidence: new DocumentConfidence(barcode.Confidence, "provider"),
                sourceOutput: sourceOutput,
                rawRepresentation: barcode);
        }
    }

    private static string ExtractPageMarkdown(
        AnalyzeResult result,
        Azure.AI.DocumentIntelligence.DocumentPage page)
        => ExtractPageMarkdown(result.Content, page.Spans);

    internal static string ExtractPageMarkdown(
        string content,
        IEnumerable<DocumentSpan> pageSpans)
    {
        DocumentSpan[] spans = pageSpans.OrderBy(span => span.Offset).ToArray();
        if (spans.Length > 0)
        {
            int first = spans[0].Offset;
            int last = spans.Max(span => span.Offset + span.Length);
            if (first >= 0 && last >= first && last <= content.Length)
            {
                return content[first..last];
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

    private static DocumentCoordinateUnit? MapUnit(LengthUnit? unit)
        => unit?.ToString() switch
        {
            "pixel" or "Pixel" => DocumentCoordinateUnit.Pixel,
            "inch" or "Inch" => DocumentCoordinateUnit.Inch,
            null => null,
            string value => throw new NotSupportedException(
                $"Azure Document Intelligence coordinate unit '{value}' is not supported."),
        };

    private static void AddFeatures(
        AnalyzeDocumentOptions options,
        IEnumerable<string>? requestedFeatures)
    {
        if (requestedFeatures is null)
        {
            return;
        }

        foreach (string feature in requestedFeatures)
        {
            options.Features.Add(feature.ToLowerInvariant() switch
            {
                "ocrhighresolution" => DocumentAnalysisFeature.OcrHighResolution,
                "languages" => DocumentAnalysisFeature.Languages,
                "barcodes" => DocumentAnalysisFeature.Barcodes,
                "formulas" => DocumentAnalysisFeature.Formulas,
                "keyvaluepairs" => DocumentAnalysisFeature.KeyValuePairs,
                "fontstyling" => DocumentAnalysisFeature.FontStyling,
                "queryfields" => DocumentAnalysisFeature.QueryFields,
                _ => throw new NotSupportedException(
                    $"Azure Document Intelligence feature '{feature}' is not supported by this adapter."),
            });
        }
    }

    private static string? NormalizeRole(ParagraphRole? role)
        => role?.ToString();

    internal static DocumentTextKind ToTextKind(ParagraphRole? role)
        => role?.ToString().Contains("heading", StringComparison.OrdinalIgnoreCase) == true
            || role?.ToString().Contains("title", StringComparison.OrdinalIgnoreCase) == true
            ? DocumentTextKind.Header
            : DocumentTextKind.Paragraph;

    private sealed record PageFrame(
        DocumentSourcePage SourcePage,
        DocumentPageReference Reference);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this)
            ? this
            : serviceType == typeof(DocumentIntelligenceClient) ? _client : null;

    public void Dispose()
    {
    }
}
