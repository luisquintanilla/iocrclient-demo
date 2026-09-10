using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using ExtractionPage = Microsoft.Extensions.DocumentExtraction.DocumentPage;
using SharedDocument = Microsoft.Extensions.Documents.Document;
using SharedImage = Microsoft.Extensions.Documents.DocumentImage;
using SharedTable = Microsoft.Extensions.Documents.DocumentTable;
using SharedTableCell = Microsoft.Extensions.Documents.DocumentTableCell;

namespace DemoOcr;

/// <summary>
/// A SECOND engine behind the same <see cref="IDocumentExtractionClient"/> contract — Azure Document Intelligence.
///
/// Different wire protocol from Mistral OCR (async-poll AnalyzeResult, not document-&gt;pages[]), so it
/// is a different class, but it normalizes onto the same DocumentExtractionResult, so the built-in reader and the
/// vector store never see the difference. This is the whole point of the capability seam: swap the
/// engine, keep the pipeline. Keyless via DefaultAzureCredential / any TokenCredential.
/// </summary>
public sealed class AzureDocumentIntelligenceClient : IDocumentExtractionClient
{
    private readonly DocumentIntelligenceClient _client;
    private readonly string _defaultModel;

    public AzureDocumentIntelligenceClient(
        Uri endpoint,
        TokenCredential credential,
        string defaultModel = "prebuilt-layout")
    {
        _client = new DocumentIntelligenceClient(endpoint, credential);
        _defaultModel = defaultModel;
    }

    public async Task<DocumentExtractionResult> ExtractAsync(
        Stream document,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        string model = options?.ModelId ?? _defaultModel;
        bool includeImages = options.GetIncludeImages();
        var analyzeOptions = new AnalyzeDocumentOptions(model, BinaryData.FromBytes(ms.ToArray()))
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
        };
        if (includeImages)
        {
            // Ask DI to render cropped figure images; each is then retrievable by figure id.
            analyzeOptions.Output.Add(AnalyzeOutputOption.Figures);
        }

        Operation<AnalyzeResult> op = await _client
            .AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken)
            .ConfigureAwait(false);

        AnalyzeResult result = op.Value;

        var entriesByPage = new Dictionary<int, List<(int Offset, DocumentNode Node)>>();
        var evidenceByPage = new Dictionary<int, List<DocumentExtractionEvidence>>();
        var pages = new List<ExtractionPage>();
        List<DocumentSpan> tableSpans = result.Tables?
            .SelectMany(static table => table.Spans)
            .ToList() ?? [];
        List<DocumentSpan> footnoteSpans = result.Tables?
            .SelectMany(static table => table.Footnotes ?? [])
            .SelectMany(static footnote => footnote.Spans)
            .ToList() ?? [];
        HashSet<int> footnoteParagraphIndexes = GetReferencedParagraphIndexes(
            result.Tables?
                .SelectMany(static table => table.Footnotes ?? [])
                .SelectMany(static footnote => footnote.Elements ?? [])
            ?? []);

        List<(int Offset, DocumentNode Node)> Entries(int pageNumber)
            => entriesByPage.TryGetValue(pageNumber, out List<(int Offset, DocumentNode Node)>? entries)
                ? entries
                : entriesByPage[pageNumber] = [];

        IReadOnlyList<DocumentNode> Nodes(int pageNumber) =>
            OrderByProviderOffset(Entries(pageNumber))
                .Select(static entry => entry.Value)
                .ToArray();

        List<DocumentExtractionEvidence> Evidence(int pageNumber)
            => evidenceByPage.TryGetValue(pageNumber, out List<DocumentExtractionEvidence>? evidence)
                ? evidence
                : evidenceByPage[pageNumber] = [];

        // Figures -> images: DI renders cropped bytes (fetched per figure id) + caption + native polygon.
        // This is the SECOND document-native engine validating DocumentImage elements (bytes + bbox + caption).
        if (includeImages && result.Figures is { Count: > 0 })
        {
            foreach (DocumentFigure figure in result.Figures)
            {
                DocumentBoundingRegion? region = ToRegion(figure.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                byte[] imageBytes = [];
                if (figure.Id is { Length: > 0 })
                {
                    Response<BinaryData> figResp = await _client
                        .GetAnalyzeResultFigureAsync(model, op.Id, figure.Id, cancellationToken)
                        .ConfigureAwait(false);
                    imageBytes = figResp.Value.ToArray();
                }

                string? description = figure.Caption?.Content;
                if (imageBytes.Length > 0 || !string.IsNullOrWhiteSpace(description))
                {
                    DocumentNodeId nodeId = DocumentExtractionDemoExtensions.CreateNodeId(
                        "document-intelligence", pageNo, "image", Entries(pageNo).Count);
                    var node = new SharedImage(
                        nodeId,
                        imageBytes,
                        imageBytes.Length > 0 ? "image/png" : null,
                        description: description,
                        pageReferences: [new(pageNo)]);
                    Entries(pageNo).Add((GetOffset(figure.Spans), node));
                    Evidence(pageNo).Add(new DocumentExtractionEvidence(nodeId)
                    {
                        BoundingRegion = region,
                        RawRepresentation = figure,
                    });
                }
            }
        }

        // Paragraphs author semantic text; geometry and provider objects remain evidence sidecars.
        if (result.Paragraphs is { Count: > 0 })
        {
            for (int paragraphIndex = 0; paragraphIndex < result.Paragraphs.Count; paragraphIndex++)
            {
                DocumentParagraph para = result.Paragraphs[paragraphIndex];
                if (!ShouldEmitParagraph(paragraphIndex, para.Spans, tableSpans, footnoteSpans, footnoteParagraphIndexes))
                {
                    continue;
                }

                DocumentBoundingRegion? region = ToRegion(para.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                DocumentTextRole role = para.Role?.ToString() switch
                {
                    "title" or "sectionHeading" => DocumentTextRole.Heading,
                    "pageHeader" => DocumentTextRole.Header,
                    "pageFooter" => DocumentTextRole.Footer,
                    _ => DocumentTextRole.Paragraph,
                };
                DocumentText node = DocumentExtractionDemoExtensions.CreateTextNode(
                    "document-intelligence",
                    pageNo,
                    Entries(pageNo).Count,
                    para.Content ?? string.Empty,
                    role);
                Entries(pageNo).Add((GetOffset(para.Spans), node));
                Evidence(pageNo).Add(new DocumentExtractionEvidence(node.Id)
                {
                    BoundingRegion = region,
                    RawRepresentation = para,
                });
            }
        }

        // Tables author typed shared cells; table geometry remains extraction evidence.
        if (result.Tables is { Count: > 0 })
        {
            foreach (Azure.AI.DocumentIntelligence.DocumentTable t in result.Tables)
            {
                DocumentBoundingRegion? region = ToRegion(t.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                int tableIndex = Entries(pageNo).Count;
                var cells = new List<SharedTableCell>(t.Cells.Count);
                foreach (Azure.AI.DocumentIntelligence.DocumentTableCell c in t.Cells)
                {
                    DocumentNodeId cellId = DocumentExtractionDemoExtensions.CreateNodeId(
                        "document-intelligence", pageNo, $"table-{tableIndex}-cell", cells.Count);
                    DocumentText cellText = new(
                        DocumentExtractionDemoExtensions.CreateNodeId(
                            "document-intelligence", pageNo, $"table-{tableIndex}-cell-text", cells.Count),
                        c.Content ?? string.Empty,
                        pageReferences: [new(pageNo)]);
                    DocumentTableCellRole cellRole = c.Kind.ToString() switch
                    {
                        "columnHeader" => DocumentTableCellRole.ColumnHeader,
                        "rowHeader" => DocumentTableCellRole.RowHeader,
                        _ => DocumentTableCellRole.Content,
                    };
                    cells.Add(new SharedTableCell(
                        cellId,
                        c.RowIndex,
                        c.ColumnIndex,
                        [cellText],
                        c.RowSpan ?? 1,
                        c.ColumnSpan ?? 1,
                        cellRole,
                        pageReferences: [new(pageNo)]));
                }
                DocumentNodeId tableId = DocumentExtractionDemoExtensions.CreateNodeId(
                    "document-intelligence", pageNo, "table", tableIndex);
                var tableNode = new SharedTable(
                    tableId,
                    t.RowCount,
                    t.ColumnCount,
                    cells,
                    pageReferences: [new(pageNo)]);
                Entries(pageNo).Add((GetOffset(t.Spans), tableNode));
                Evidence(pageNo).Add(new DocumentExtractionEvidence(tableId)
                {
                    BoundingRegion = region,
                    RawRepresentation = t,
                });

                if (t.Caption is { Content.Length: > 0 } caption)
                {
                    int captionPage = ToRegion(caption.BoundingRegions)?.PageNumber ?? pageNo;
                    DocumentText captionNode = DocumentExtractionDemoExtensions.CreateTextNode(
                        "document-intelligence",
                        captionPage,
                        Entries(captionPage).Count,
                        caption.Content,
                        DocumentTextRole.Caption);
                    Entries(captionPage).Add((GetOffset(caption.Spans), captionNode));
                    Evidence(captionPage).Add(new DocumentExtractionEvidence(captionNode.Id)
                    {
                        BoundingRegion = ToRegion(caption.BoundingRegions),
                        RawRepresentation = caption,
                    });
                }

                if (t.Footnotes is { Count: > 0 })
                {
                    foreach (DocumentFootnote footnote in t.Footnotes)
                    {
                        int footnotePage = ToRegion(footnote.BoundingRegions)?.PageNumber ?? pageNo;
                        DocumentText footnoteNode = DocumentExtractionDemoExtensions.CreateTextNode(
                            "document-intelligence",
                            footnotePage,
                            Entries(footnotePage).Count,
                            footnote.Content);
                        Entries(footnotePage).Add((GetOffset(footnote.Spans), footnoteNode));
                        Evidence(footnotePage).Add(new DocumentExtractionEvidence(footnoteNode.Id)
                        {
                            BoundingRegion = ToRegion(footnote.BoundingRegions),
                            RawRepresentation = footnote,
                        });
                    }
                }
            }
        }

        if (result.Pages is { Count: > 0 })
        {
            for (int i = 0; i < result.Pages.Count; i++)
            {
                Azure.AI.DocumentIntelligence.DocumentPage page = result.Pages[i];
                int pageNo = page.PageNumber;
                pages.Add(new ExtractionPage(
                    pageNo,
                    DocumentExtractionDemoExtensions.CreatePageDocument(
                        "document-intelligence",
                        pageNo,
                        Nodes(pageNo)),
                    markdown: SliceContent(result.Content, page.Spans),
                    evidence: Evidence(pageNo))
                {
                    Dimensions = page.Width is { } w && page.Height is { } h ? new DocumentPageDimensions((float)w, (float)h) : null,
                    CoordinateUnit = ToCoordinateUnit(page.Unit),
                    RawRepresentation = page,
                    AdditionalProperties = new() { ["di.pageNumber"] = pageNo },
                });
            }
        }
        else
        {
            string projected = DocumentExtractionDemoExtensions.ProjectProviderMarkdown(result.Content ?? string.Empty);
            IReadOnlyList<DocumentNode> nodes = projected.Length > 0
                ? [DocumentExtractionDemoExtensions.CreateTextNode("document-intelligence", 1, 0, projected)]
                : [];
            pages.Add(new ExtractionPage(
                1,
                DocumentExtractionDemoExtensions.CreatePageDocument("document-intelligence", 1, nodes),
                markdown: result.Content));
        }

        var tableCount = result.Tables?.Count ?? 0;

        return new DocumentExtractionResult(pages)
        {
            Usage = new DocumentExtractionUsage { PagesProcessed = result.Pages?.Count },
            RawRepresentation = result,
            AdditionalProperties = new() { ["modelId"] = model, ["di.tableCount"] = tableCount },
        };
    }

    private static DocumentCoordinateUnit? ToCoordinateUnit(object? unit)
        => unit?.ToString() switch
        {
            "pixel" => DocumentCoordinateUnit.Pixel,
            "point" => DocumentCoordinateUnit.Point,
            "inch" => DocumentCoordinateUnit.Inch,
            _ => null,
        };

    private static int GetOffset(IEnumerable<DocumentSpan>? spans) =>
        spans?.Select(static span => span.Offset).DefaultIfEmpty(int.MaxValue).Min() ?? int.MaxValue;

    public static bool ShouldEmitParagraph(
        int paragraphIndex,
        IEnumerable<DocumentSpan> paragraphSpans,
        IEnumerable<DocumentSpan> tableSpans,
        IEnumerable<DocumentSpan> footnoteSpans,
        IReadOnlySet<int> footnoteParagraphIndexes)
        => !footnoteParagraphIndexes.Contains(paragraphIndex) &&
            !paragraphSpans.Any(paragraphSpan =>
                tableSpans.Concat(footnoteSpans).Any(excludedSpan =>
                    Overlaps(paragraphSpan, excludedSpan)));

    public static HashSet<int> GetReferencedParagraphIndexes(IEnumerable<string> elements)
        => elements
            .Select(TryGetParagraphIndex)
            .Where(static index => index is not null)
            .Select(static index => index!.Value)
            .ToHashSet();

    public static IReadOnlyList<(int Offset, T Value)> OrderByProviderOffset<T>(
        IEnumerable<(int Offset, T Value)> entries)
        => entries.OrderBy(static entry => entry.Offset).ToArray();

    private static int? TryGetParagraphIndex(string element)
    {
        const string Prefix = "/paragraphs/";
        return element.StartsWith(Prefix, StringComparison.Ordinal) &&
            int.TryParse(element.AsSpan(Prefix.Length), out int index)
                ? index
                : null;
    }

    private static bool Overlaps(DocumentSpan left, DocumentSpan right)
        => left.Offset < right.Offset + right.Length &&
            right.Offset < left.Offset + left.Length;

    private static string? SliceContent(string? content, IEnumerable<DocumentSpan>? spans)
    {
        if (string.IsNullOrEmpty(content) || spans is null)
        {
            return null;
        }

        string pageContent = string.Concat(spans
            .OrderBy(static span => span.Offset)
            .Where(span => span.Offset >= 0 && span.Length > 0 && span.Offset + span.Length <= content.Length)
            .Select(span => content.Substring(span.Offset, span.Length)));
        return pageContent.Length > 0 ? pageContent : null;
    }

    /// <summary>Map a DI BoundingRegions list onto the SHARED region primitive, keeping the native polygon.</summary>
    private static DocumentBoundingRegion? ToRegion(IReadOnlyList<BoundingRegion>? regions)
    {
        if (regions is not { Count: > 0 })
        {
            return null;
        }
        BoundingRegion r = regions[0];
        var polygon = new List<DocumentPoint>(r.Polygon.Count / 2);
        for (int i = 0; i + 1 < r.Polygon.Count; i += 2)
        {
            polygon.Add(new DocumentPoint((float)r.Polygon[i], (float)r.Polygon[i + 1]));
        }
        return new DocumentBoundingRegion(r.PageNumber, polygon);
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => DocumentExtractionDemoExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(DocumentIntelligenceClient) ? _client : null;

    public void Dispose() { }
}
