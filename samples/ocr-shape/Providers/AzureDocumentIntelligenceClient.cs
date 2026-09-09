using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using DocumentElement = Microsoft.Extensions.DocumentExtraction.DocumentElement;
using DocumentPage = Microsoft.Extensions.DocumentExtraction.DocumentPage;
using DocumentTable = Microsoft.Extensions.DocumentExtraction.DocumentTable;
using DocumentTableCell = Microsoft.Extensions.DocumentExtraction.DocumentTableCell;
using DocumentTableCellKind = Microsoft.Extensions.DocumentExtraction.DocumentTableCellKind;

namespace DemoOcr;

/// <summary>
/// A SECOND engine behind the same <see cref="IDocumentExtractionClient"/> contract — Azure Document Intelligence.
///
/// Different wire protocol from Mistral OCR (async-poll AnalyzeResult, not document-&gt;pages[]), so it
/// is a different class, but it normalizes onto the same DocumentExtractionResult, so DocumentExtractionReader and the
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

        // DI returns one markdown string + a flat list of pages. Map onto DocumentExtractionResult pages.
        var pages = new List<DocumentPage>();
        var blocksByPage = new Dictionary<int, List<DocumentBlock>>();
        var tablesByPage = new Dictionary<int, List<DocumentTable>>();
        var imagesByPage = new Dictionary<int, List<DocumentImage>>();

        // Figures -> images: DI renders cropped bytes (fetched per figure id) + caption + native polygon.
        // This is the SECOND document-native engine validating DocumentImage elements (bytes + bbox + caption).
        if (includeImages && result.Figures is { Count: > 0 })
        {
            foreach (DocumentFigure figure in result.Figures)
            {
                DocumentBoundingRegion? region = ToRegion(figure.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                var image = new DocumentImage
                {
                    BoundingRegion = region,
                    Caption = figure.Caption?.Content,
                };
                if (figure.Id is { Length: > 0 })
                {
                    Response<BinaryData> figResp = await _client
                        .GetAnalyzeResultFigureAsync(model, op.Id, figure.Id, cancellationToken)
                        .ConfigureAwait(false);
                    image.Content = figResp.Value.ToArray();
                    image.MediaType = "image/png";
                }

                (imagesByPage.TryGetValue(pageNo, out var imgList) ? imgList : imagesByPage[pageNo] = new())
                    .Add(image);
            }
        }

        // Paragraphs -> blocks, carrying NATIVE DI polygons (no rect flattening — the reason bbox is a polygon).
        if (result.Paragraphs is { Count: > 0 })
        {
            foreach (DocumentParagraph para in result.Paragraphs)
            {
                DocumentBoundingRegion? region = ToRegion(para.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                (blocksByPage.TryGetValue(pageNo, out var list) ? list : blocksByPage[pageNo] = new())
                    .Add(new DocumentBlock(para.Content ?? "")
                    {
                        Kind = para.Role?.ToString() is { Length: > 0 } role ? new DocumentBlockKind(role) : null,
                        BoundingRegion = region,
                    });
            }
        }

        // Tables -> structured cells (the Azure DI shape: indices + spans + kind) + native polygon.
        if (result.Tables is { Count: > 0 })
        {
            foreach (Azure.AI.DocumentIntelligence.DocumentTable t in result.Tables)
            {
                DocumentBoundingRegion? region = ToRegion(t.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                var cells = new List<DocumentTableCell>(t.Cells.Count);
                foreach (Azure.AI.DocumentIntelligence.DocumentTableCell c in t.Cells)
                {
                    cells.Add(new DocumentTableCell(
                        c.RowIndex,
                        c.ColumnIndex,
                        [new DocumentBlock(c.Content ?? "")])
                    {
                        Kind = c.Kind.ToString() is { Length: > 0 } cellKind ? new DocumentTableCellKind(cellKind) : null,
                        RowSpan = c.RowSpan ?? 1,
                        ColumnSpan = c.ColumnSpan ?? 1,
                    });
                }
                (tablesByPage.TryGetValue(pageNo, out var list) ? list : tablesByPage[pageNo] = new())
                    .Add(new DocumentTable(t.RowCount, t.ColumnCount, cells) { BoundingRegion = region });
            }
        }

        if (result.Pages is { Count: > 0 })
        {
            for (int i = 0; i < result.Pages.Count; i++)
            {
                Azure.AI.DocumentIntelligence.DocumentPage page = result.Pages[i];
                int pageNo = page.PageNumber;
                var elements = new List<DocumentElement>();
                if (blocksByPage.TryGetValue(pageNo, out var b))
                {
                    elements.AddRange(b);
                }
                if (tablesByPage.TryGetValue(pageNo, out var tb))
                {
                    elements.AddRange(tb.Cast<DocumentElement>());
                }
                if (imagesByPage.TryGetValue(pageNo, out var im))
                {
                    elements.AddRange(im);
                }
                pages.Add(new DocumentPage(pageNo, elements, i == 0 ? result.Content ?? "" : "")
                {
                    Dimensions = page.Width is { } w && page.Height is { } h ? new DocumentPageDimensions((float)w, (float)h) : null,
                    CoordinateUnit = ToCoordinateUnit(page.Unit),
                    AdditionalProperties = new() { ["di.pageNumber"] = pageNo },
                });
            }
        }
        else
        {
            pages.Add(new DocumentPage(1, [], result.Content ?? ""));
        }

        var tableCount = result.Tables?.Count ?? 0;

        return new DocumentExtractionResult(pages)
        {
            RawRepresentation = result,
            AdditionalProperties = new() { ["modelId"] = model, ["di.tableCount"] = tableCount },
        };
    }

    private static DocumentCoordinateUnit? ToCoordinateUnit(object? unit)
        => unit?.ToString() switch
        {
            "Pixel" => DocumentCoordinateUnit.Pixel,
            "Point" => DocumentCoordinateUnit.Point,
            "Inch" => DocumentCoordinateUnit.Inch,
            _ => null,
        };

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
        => OcrShapeExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(DocumentIntelligenceClient) ? _client : null;

    public void Dispose() { }
}
