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
    private readonly DocumentIntelligenceClient _client;
    private readonly string _defaultModel;
    private readonly bool _includeImages;

    public AzureDocumentIntelligenceClient(
        Uri endpoint,
        TokenCredential credential,
        string defaultModel = "prebuilt-layout",
        bool includeImages = false)
    {
        _client = new DocumentIntelligenceClient(endpoint, credential);
        _defaultModel = defaultModel;
        _includeImages = includeImages;
    }

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamPages(
            ct => ExtractPagesCoreAsync(document, mediaType, cancellationToken: ct),
            cancellationToken);

    private async Task<IReadOnlyList<DocumentExtractionPage>> ExtractPagesCoreAsync(
        Stream document,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        string model = _defaultModel;
        var analyzeOptions = new AnalyzeDocumentOptions(model, BinaryData.FromBytes(ms.ToArray()))
        {
            OutputContentFormat = DocumentContentFormat.Markdown,
        };
        if (_includeImages)
        {
            analyzeOptions.Output.Add(AnalyzeOutputOption.Figures);
        }

        Operation<AnalyzeResult> operation = await _client
            .AnalyzeDocumentAsync(WaitUntil.Completed, analyzeOptions, cancellationToken)
            .ConfigureAwait(false);
        AnalyzeResult result = operation.Value;

        var observationsByPage = new Dictionary<int, List<DocumentExtractionObservation>>();
        int paragraphIndex = 0;
        if (result.Paragraphs is { Count: > 0 })
        {
            foreach (DocumentParagraph paragraph in result.Paragraphs)
            {
                int pageNumber = paragraph.BoundingRegions is { Count: > 0 }
                    ? paragraph.BoundingRegions[0].PageNumber
                    : 1;
                string text = paragraph.Content ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                AddObservation(
                    observationsByPage,
                    pageNumber,
                    new DocumentExtractionText(
                        $"di:paragraph:{paragraphIndex++}",
                        GetOrder(observationsByPage, pageNumber),
                        ToTextKind(paragraph.Role?.ToString()),
                        text,
                        text,
                        pageReferences: [PageReference(pageNumber)]));
            }
        }

        int tableIndex = 0;
        if (result.Tables is { Count: > 0 })
        {
            foreach (Azure.AI.DocumentIntelligence.DocumentTable table in result.Tables)
            {
                int pageNumber = table.BoundingRegions is { Count: > 0 }
                    ? table.BoundingRegions[0].PageNumber
                    : 1;
                var cells = new List<DocumentExtractionTableCell>(table.Cells.Count);
                foreach (Azure.AI.DocumentIntelligence.DocumentTableCell cell in table.Cells)
                {
                    string text = cell.Content ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    cells.Add(new DocumentExtractionTableCell(
                        cell.RowIndex,
                        cell.ColumnIndex,
                        new DocumentExtractionText(
                            $"di:table:{tableIndex}:cell:{cell.RowIndex}:{cell.ColumnIndex}",
                            cell.RowIndex * Math.Max(table.ColumnCount, 1) + cell.ColumnIndex,
                            DocumentTextKind.Paragraph,
                            text,
                            text,
                            pageReferences: [PageReference(pageNumber)])));
                }

                AddObservation(
                    observationsByPage,
                    pageNumber,
                    new DocumentExtractionTable(
                        $"di:table:{tableIndex++}",
                        GetOrder(observationsByPage, pageNumber),
                        BuildTableMarkdown(table.RowCount, table.ColumnCount, cells),
                        table.RowCount,
                        table.ColumnCount,
                        cells,
                        pageReferences: [PageReference(pageNumber)]));
            }
        }

        if (_includeImages && result.Figures is { Count: > 0 })
        {
            int imageIndex = 0;
            foreach (DocumentFigure figure in result.Figures)
            {
                int pageNumber = figure.BoundingRegions is { Count: > 0 }
                    ? figure.BoundingRegions[0].PageNumber
                    : 1;
                ReadOnlyMemory<byte>? content = null;
                if (figure.Id is { Length: > 0 })
                {
                    Response<BinaryData> figureResponse = await _client
                        .GetAnalyzeResultFigureAsync(model, operation.Id, figure.Id, cancellationToken)
                        .ConfigureAwait(false);
                    content = figureResponse.Value.ToMemory().ToArray();
                }

                AddObservation(
                    observationsByPage,
                    pageNumber,
                    new DocumentExtractionImage(
                        $"di:image:{imageIndex++}",
                        GetOrder(observationsByPage, pageNumber),
                        "[image]",
                        content: content,
                        mediaType: content is null ? null : "image/png",
                        alternativeText: figure.Caption?.Content,
                        pageReferences: [PageReference(pageNumber)]));
            }
        }

        var pages = new List<DocumentExtractionPage>();
        if (result.Pages is { Count: > 0 })
        {
            foreach (Azure.AI.DocumentIntelligence.DocumentPage page in result.Pages)
            {
                observationsByPage.TryGetValue(page.PageNumber, out List<DocumentExtractionObservation>? observations);
                pages.Add(new DocumentExtractionPage(
                    page.PageNumber,
                    observations ?? [],
                    page.PageNumber == result.Pages[0].PageNumber ? result.Content : null,
                    [PageReference(page.PageNumber)]));
            }
        }
        else
        {
            pages.Add(new DocumentExtractionPage(
                1,
                observationsByPage.TryGetValue(1, out List<DocumentExtractionObservation>? observations)
                    ? observations
                    : [],
                result.Content,
                [PageReference(1)]));
        }

        return pages;
    }

    private static void AddObservation(
        Dictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        int pageNumber,
        DocumentExtractionObservation observation)
    {
        if (!observationsByPage.TryGetValue(pageNumber, out List<DocumentExtractionObservation>? observations))
        {
            observations = [];
            observationsByPage.Add(pageNumber, observations);
        }

        observations.Add(observation);
    }

    private static int GetOrder(
        Dictionary<int, List<DocumentExtractionObservation>> observationsByPage,
        int pageNumber)
        => observationsByPage.TryGetValue(pageNumber, out List<DocumentExtractionObservation>? observations)
            ? observations.Count
            : 0;

    private static DocumentPageReference PageReference(int pageNumber) => new($"page:{pageNumber}");

    private static DocumentTextKind ToTextKind(string? role)
        => role?.Contains("heading", StringComparison.OrdinalIgnoreCase) == true
            || role?.Contains("title", StringComparison.OrdinalIgnoreCase) == true
            ? DocumentTextKind.Header
            : DocumentTextKind.Paragraph;

    private static string BuildTableMarkdown(
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
            values[cell.Row, cell.Column] = cell.Content.Markdown;
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

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this)
            ? this
            : serviceType == typeof(DocumentIntelligenceClient) ? _client : null;

    public void Dispose() { }
}
