using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;

using Microsoft.Extensions.AI;

namespace DemoOcr;

/// <summary>
/// A SECOND engine behind the same <see cref="IOcrClient"/> contract — Azure Document Intelligence.
///
/// Different wire protocol from Mistral OCR (async-poll AnalyzeResult, not document-&gt;pages[]), so it
/// is a different class — but it normalizes onto the same OcrResult, so the OcrDocumentReader and the
/// vector store never see the difference. This is the whole point of the capability seam: swap the
/// engine, keep the pipeline. Keyless via DefaultAzureCredential / any TokenCredential.
/// </summary>
public sealed class AzureDocumentIntelligenceClient : IOcrClient
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

    public async Task<OcrResult> ExtractAsync(
        Stream document,
        string mediaType,
        OcrOptions? options = null,
        IProgress<OcrProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        string model = options?.ModelId ?? _defaultModel;
        bool includeImages = options?.IncludeImages ?? false;
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

        // DI returns one markdown string + a flat list of pages. Map onto OcrResult pages.
        var pages = new List<OcrPage>();
        var blocksByPage = new Dictionary<int, List<OcrBlock>>();
        var tablesByPage = new Dictionary<int, List<OcrTable>>();
        var imagesByPage = new Dictionary<int, List<OcrImage>>();

        // Figures -> images: DI renders cropped bytes (fetched per figure id) + caption + native polygon.
        // This is the SECOND document-native engine validating OcrPage.Images (bytes + bbox + caption).
        if (includeImages && result.Figures is { Count: > 0 })
        {
            foreach (DocumentFigure figure in result.Figures)
            {
                OcrBoundingRegion? region = ToRegion(figure.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                var image = new OcrImage
                {
                    BoundingRegion = region,
                    Caption = figure.Caption?.Content,
                };
                if (figure.Id is { Length: > 0 })
                {
                    Response<BinaryData> figResp = await _client
                        .GetAnalyzeResultFigureAsync(model, op.Id, figure.Id, cancellationToken)
                        .ConfigureAwait(false);
                    image.Content = new DataContent(figResp.Value.ToArray(), "image/png");
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
                OcrBoundingRegion? region = ToRegion(para.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                (blocksByPage.TryGetValue(pageNo, out var list) ? list : blocksByPage[pageNo] = new())
                    .Add(new OcrBlock(para.Content ?? "")
                    {
                        Kind = para.Role?.ToString(),
                        BoundingRegion = region,
                    });
            }
        }

        // Tables -> structured cells (the Azure DI shape: indices + spans + kind) + native polygon.
        if (result.Tables is { Count: > 0 })
        {
            foreach (DocumentTable t in result.Tables)
            {
                OcrBoundingRegion? region = ToRegion(t.BoundingRegions);
                int pageNo = region?.PageNumber ?? 1;
                var cells = new List<OcrTableCell>(t.Cells.Count);
                foreach (DocumentTableCell c in t.Cells)
                {
                    cells.Add(new OcrTableCell(c.RowIndex, c.ColumnIndex, c.Content ?? "")
                    {
                        Kind = c.Kind.ToString(),
                        RowSpan = c.RowSpan ?? 1,
                        ColumnSpan = c.ColumnSpan ?? 1,
                    });
                }
                (tablesByPage.TryGetValue(pageNo, out var list) ? list : tablesByPage[pageNo] = new())
                    .Add(new OcrTable(t.RowCount, t.ColumnCount, cells) { BoundingRegion = region });
            }
        }

        if (result.Pages is { Count: > 0 })
        {
            for (int i = 0; i < result.Pages.Count; i++)
            {
                DocumentPage page = result.Pages[i];
                int pageNo = page.PageNumber;
                pages.Add(new OcrPage(i, i == 0 ? result.Content ?? "" : "")
                {
                    Confidence = null,
                    Blocks = blocksByPage.TryGetValue(pageNo, out var b) ? b : [],
                    Tables = tablesByPage.TryGetValue(pageNo, out var tb) ? tb : [],
                    Images = imagesByPage.TryGetValue(pageNo, out var im) ? im : [],
                    AdditionalProperties = new() { ["di.pageNumber"] = pageNo },
                });
                progress?.Report(new OcrProgress
                {
                    PagesProcessed = i + 1,
                    TotalPages = result.Pages.Count,
                    Status = "analyzing",
                });
            }
        }
        else
        {
            pages.Add(new OcrPage(0, result.Content ?? ""));
        }

        var tableCount = result.Tables?.Count ?? 0;

        return new OcrResult(pages)
        {
            OcrSource = "azure-document-intelligence",
            ModelId = model,
            Usage = new OcrUsage { PagesProcessed = result.Pages?.Count },
            RawRepresentation = result,
            AdditionalProperties = new() { ["di.tableCount"] = tableCount },
        };
    }

    /// <summary>Map a DI BoundingRegions list onto the SHARED region primitive, keeping the native polygon.</summary>
    private static OcrBoundingRegion? ToRegion(IReadOnlyList<BoundingRegion>? regions)
    {
        if (regions is not { Count: > 0 })
        {
            return null;
        }
        BoundingRegion r = regions[0];
        return new OcrBoundingRegion(r.PageNumber, r.Polygon);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(DocumentIntelligenceClient) ? _client : null;

    public void Dispose() { }
}
