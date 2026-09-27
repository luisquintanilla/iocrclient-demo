using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by Azure AI Content Understanding.
/// </summary>
public sealed class ContentUnderstandingClient : IDocumentExtractionClient
{
    public const string DefaultAnalyzerId = "prebuilt-document";

    private readonly Azure.AI.ContentUnderstanding.ContentUnderstandingClient _client;
    private readonly string _analyzerId;

    public ContentUnderstandingClient(
        Uri endpoint,
        TokenCredential credential,
        string analyzerId = DefaultAnalyzerId)
    {
        _client = new Azure.AI.ContentUnderstanding.ContentUnderstandingClient(endpoint, credential);
        _analyzerId = analyzerId;
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

        string analyzerId = _analyzerId;
        Operation<AnalysisResult> operation = await _client
            .AnalyzeBinaryAsync(
                WaitUntil.Completed,
                analyzerId,
                BinaryData.FromBytes(ms.ToArray()),
                contentRange: null,
                contentType: mediaType,
                processingLocation: null,
                cancellationToken)
            .ConfigureAwait(false);

        var observationsByPage = new Dictionary<int, List<DocumentExtractionObservation>>();
        int contentIndex = 0;
        foreach (AnalysisContent content in operation.Value.Contents)
        {
            if (content is not DocumentContent documentContent)
            {
                continue;
            }

            int contentPage = NormalizePageNumber(documentContent.StartPageNumber);
            if (!string.IsNullOrWhiteSpace(documentContent.Markdown))
            {
                AddObservation(
                    observationsByPage,
                    contentPage,
                    new DocumentExtractionText(
                        $"cu:content:{contentIndex}:text",
                        GetOrder(observationsByPage, contentPage),
                        DocumentTextKind.Paragraph,
                        documentContent.Markdown,
                        documentContent.Markdown,
                        pageReferences: [PageReference(contentPage)]));
            }

            if (documentContent.Tables is { Count: > 0 })
            {
                int tableIndex = 0;
                foreach (Azure.AI.ContentUnderstanding.DocumentTable table in documentContent.Tables)
                {
                    var cells = new List<DocumentExtractionTableCell>(table.Cells.Count);
                    foreach (Azure.AI.ContentUnderstanding.DocumentTableCell cell in table.Cells)
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
                                $"cu:content:{contentIndex}:table:{tableIndex}:cell:{cell.RowIndex}:{cell.ColumnIndex}",
                                cell.RowIndex * Math.Max(table.ColumnCount, 1) + cell.ColumnIndex,
                                DocumentTextKind.Paragraph,
                                text,
                                text,
                                pageReferences: [PageReference(contentPage)])));
                    }

                    AddObservation(
                        observationsByPage,
                        contentPage,
                        new DocumentExtractionTable(
                            $"cu:content:{contentIndex}:table:{tableIndex++}",
                            GetOrder(observationsByPage, contentPage),
                            BuildTableMarkdown(table.RowCount, table.ColumnCount, cells),
                            table.RowCount,
                            table.ColumnCount,
                            cells,
                            pageReferences: [PageReference(contentPage)]));
                }
            }

            contentIndex++;
        }

        var pages = new List<DocumentExtractionPage>();
        foreach (AnalysisContent content in operation.Value.Contents)
        {
            if (content is not DocumentContent documentContent)
            {
                continue;
            }

            if (documentContent.Pages is { Count: > 0 })
            {
                foreach (Azure.AI.ContentUnderstanding.DocumentPage page in documentContent.Pages)
                {
                    observationsByPage.TryGetValue(page.PageNumber, out List<DocumentExtractionObservation>? observations);
                    pages.Add(new DocumentExtractionPage(
                        page.PageNumber,
                        observations ?? [],
                        page.PageNumber == documentContent.StartPageNumber ? documentContent.Markdown : null,
                        [PageReference(page.PageNumber)]));
                }
            }
            else
            {
                observationsByPage.TryGetValue(NormalizePageNumber(documentContent.StartPageNumber),
                    out List<DocumentExtractionObservation>? observations);
                int pageNumber = NormalizePageNumber(documentContent.StartPageNumber);
                pages.Add(new DocumentExtractionPage(
                    pageNumber,
                    observations ?? [],
                    documentContent.Markdown,
                    [PageReference(pageNumber)]));
            }
        }

        if (pages.Count == 0)
        {
            pages.Add(new DocumentExtractionPage(1, [], null, [PageReference(1)]));
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

    public static int NormalizePageNumber(int providerPageNumber)
        => Math.Max(providerPageNumber, 1);

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
            : serviceType == typeof(Azure.AI.ContentUnderstanding.ContentUnderstandingClient) ? _client : null;

    public void Dispose() { }
}
