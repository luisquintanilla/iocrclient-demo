using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using ExtractionPage = Microsoft.Extensions.DocumentExtraction.DocumentPage;
using SharedTable = Microsoft.Extensions.Documents.DocumentTable;
using SharedTableCell = Microsoft.Extensions.Documents.DocumentTableCell;

namespace DemoOcr;

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

    public async Task<DocumentExtractionResult> ExtractAsync(
        Stream document,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        string analyzerId = options?.ModelId ?? _analyzerId;
        Operation<AnalysisResult> operation = await _client
            .AnalyzeBinaryAsync(
                WaitUntil.Completed,
                analyzerId,
                BinaryData.FromBytes(buffer.ToArray()),
                contentRange: null,
                contentType: mediaType,
                processingLocation: null,
                cancellationToken)
            .ConfigureAwait(false);

        AnalysisResult result = operation.Value;
        var pages = new List<ExtractionPage>();

        foreach (AnalysisContent content in result.Contents)
        {
            if (content is not DocumentContent providerDocument)
            {
                continue;
            }

            string providerMarkdown = providerDocument.Markdown ?? string.Empty;
            var entriesByPage =
                new Dictionary<int, List<(int Offset, DocumentNode Node, DocumentExtractionEvidence? Evidence)>>();
            var tableSpans = providerDocument.Tables?
                .Select(static table => table.Span)
                .ToList() ?? [];

            List<(int Offset, DocumentNode Node, DocumentExtractionEvidence? Evidence)> Entries(int pageNumber)
                => entriesByPage.TryGetValue(pageNumber, out var entries)
                    ? entries
                    : entriesByPage[pageNumber] = [];

            if (providerDocument.Paragraphs is { Count: > 0 })
            {
                foreach (Azure.AI.ContentUnderstanding.DocumentParagraph paragraph in providerDocument.Paragraphs)
                {
                    if (tableSpans.Any(tableSpan =>
                        paragraph.Span.Offset < tableSpan.Offset + tableSpan.Length &&
                        tableSpan.Offset < paragraph.Span.Offset + paragraph.Span.Length))
                    {
                        continue;
                    }

                    int[] sourcePages = GetSourcePages(paragraph.Source, providerDocument.StartPageNumber);
                    int pageNumber = sourcePages[0];
                    DocumentPageReference[] pageReferences = PageReferences(sourcePages);
                    DocumentTextRole role = paragraph.Role?.ToString() switch
                    {
                        "title" or "sectionHeading" => DocumentTextRole.Heading,
                        "pageHeader" => DocumentTextRole.Header,
                        "pageFooter" => DocumentTextRole.Footer,
                        _ => DocumentTextRole.Paragraph,
                    };
                    DocumentText node = new(
                        DocumentExtractionDemoExtensions.CreateNodeId(
                            "content-understanding", pageNumber, "text", Entries(pageNumber).Count),
                        paragraph.Content ?? string.Empty,
                        role,
                        pageReferences: pageReferences);
                    Entries(pageNumber).Add((
                        paragraph.Span.Offset,
                        node,
                        CreateEvidence(node.Id, paragraph, paragraph.Source)));
                }
            }

            if (providerDocument.Tables is { Count: > 0 })
            {
                int tableIndex = 0;
                foreach (Azure.AI.ContentUnderstanding.DocumentTable providerTable in providerDocument.Tables)
                {
                    int currentTableIndex = tableIndex++;
                    int[] sourcePages = GetSourcePages(providerTable.Source, providerDocument.StartPageNumber);
                    int pageNumber = sourcePages[0];
                    DocumentPageReference[] pageReferences = PageReferences(sourcePages);
                    var cells = new List<SharedTableCell>(providerTable.Cells.Count);
                    foreach (Azure.AI.ContentUnderstanding.DocumentTableCell providerCell in providerTable.Cells)
                    {
                        int cellIndex = cells.Count;
                        DocumentText cellText = new(
                            DocumentExtractionDemoExtensions.CreateNodeId(
                                "content-understanding", pageNumber, $"table-{currentTableIndex}-cell-text", cellIndex),
                            providerCell.Content ?? string.Empty,
                            pageReferences: pageReferences);
                        DocumentTableCellRole role = providerCell.Kind?.ToString() switch
                        {
                            "columnHeader" => DocumentTableCellRole.ColumnHeader,
                            "rowHeader" => DocumentTableCellRole.RowHeader,
                            _ => DocumentTableCellRole.Content,
                        };
                        cells.Add(new SharedTableCell(
                            DocumentExtractionDemoExtensions.CreateNodeId(
                                "content-understanding", pageNumber, $"table-{currentTableIndex}-cell", cellIndex),
                            providerCell.RowIndex,
                            providerCell.ColumnIndex,
                            [cellText],
                            providerCell.RowSpan ?? 1,
                            providerCell.ColumnSpan ?? 1,
                            role,
                            pageReferences: pageReferences));
                    }

                    DocumentNodeId tableId = DocumentExtractionDemoExtensions.CreateNodeId(
                        "content-understanding", pageNumber, "table", currentTableIndex);
                    var table = new SharedTable(
                        tableId,
                        providerTable.RowCount,
                        providerTable.ColumnCount,
                        cells,
                        pageReferences: pageReferences);
                    Entries(pageNumber).Add((
                        providerTable.Span.Offset,
                        table,
                        CreateEvidence(tableId, providerTable, providerTable.Source)));
                    if (!string.IsNullOrWhiteSpace(providerTable.Caption?.Content))
                    {
                        Entries(pageNumber).Add((
                            providerTable.Caption.Span.Offset,
                            new DocumentText(
                                DocumentExtractionDemoExtensions.CreateNodeId(
                                    "content-understanding", pageNumber, "table-caption", currentTableIndex),
                                providerTable.Caption.Content,
                                DocumentTextRole.Caption,
                                pageReferences: pageReferences),
                            null));
                    }

                    if (providerTable.Footnotes is { Count: > 0 })
                    {
                        for (int footnoteIndex = 0; footnoteIndex < providerTable.Footnotes.Count; footnoteIndex++)
                        {
                            DocumentFootnote footnote = providerTable.Footnotes[footnoteIndex];
                            Entries(pageNumber).Add((
                                footnote.Span.Offset,
                                new DocumentText(
                                    DocumentExtractionDemoExtensions.CreateNodeId(
                                        "content-understanding",
                                        pageNumber,
                                        $"table-{currentTableIndex}-footnote",
                                        footnoteIndex),
                                    footnote.Content,
                                    pageReferences: pageReferences),
                                null));
                        }
                    }
                }
            }

            bool hasProviderPages = providerDocument.Pages is { Count: > 0 };
            IEnumerable<Azure.AI.ContentUnderstanding.DocumentPage> providerPages =
                hasProviderPages
                    ? providerDocument.Pages
                    : [ContentUnderstandingModelFactory.DocumentPage(providerDocument.StartPageNumber)];

            foreach (Azure.AI.ContentUnderstanding.DocumentPage providerPage in providerPages)
            {
                if (providerDocument.Paragraphs is not { Count: > 0 })
                {
                    IEnumerable<ContentSpan> pageSpans =
                        providerPage.Spans is { Count: > 0 }
                            ? providerPage.Spans
                            : !hasProviderPages && providerMarkdown.Length > 0
                                ? [ContentUnderstandingModelFactory.ContentSpan(0, providerMarkdown.Length)]
                                : [];
                    AddMarkdownSegments(
                        providerMarkdown,
                        pageSpans,
                        tableSpans,
                        providerPage.PageNumber,
                        Entries(providerPage.PageNumber));
                }

                var orderedEntries = Entries(providerPage.PageNumber)
                    .OrderBy(static entry => entry.Offset)
                    .ToArray();
                string pageMarkdown = providerPage.Spans is { Count: > 0 }
                    ? SliceMarkdown(providerMarkdown, providerPage.Spans)
                    : !hasProviderPages
                        ? providerMarkdown
                        : string.Empty;
                pages.Add(new ExtractionPage(
                    providerPage.PageNumber,
                    DocumentExtractionDemoExtensions.CreatePageDocument(
                        "content-understanding",
                        providerPage.PageNumber,
                        orderedEntries.Select(static entry => entry.Node).ToArray()),
                    markdown: pageMarkdown.Length > 0 ? pageMarkdown : null,
                    evidence: orderedEntries
                        .Where(static entry => entry.Evidence is not null)
                        .Select(static entry => entry.Evidence!)
                        .ToArray())
                {
                    RawRepresentation = providerPage,
                    AdditionalProperties = new() { ["cu.pageNumber"] = providerPage.PageNumber },
                });
            }
        }

        if (pages.Count == 0)
        {
            pages.Add(new ExtractionPage(
                1,
                DocumentExtractionDemoExtensions.CreatePageDocument("content-understanding", 1, [])));
        }

        return new DocumentExtractionResult(pages)
        {
            RawRepresentation = result,
            AdditionalProperties = new() { ["modelId"] = analyzerId },
        };
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => DocumentExtractionDemoExtensions.StreamAsUpdates(
            ct => ExtractAsync(document, mediaType, options, ct),
            cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(Azure.AI.ContentUnderstanding.ContentUnderstandingClient) ? _client
            : null;

    public void Dispose()
    {
    }

    private static DocumentExtractionEvidence CreateEvidence(
        DocumentNodeId nodeId,
        object rawRepresentation,
        string? source)
        => new(nodeId)
        {
            RawRepresentation = rawRepresentation,
            AdditionalProperties = string.IsNullOrWhiteSpace(source)
                ? null
                : new() { ["cu.source"] = source },
        };

    private static int[] GetSourcePages(string? source, int fallbackPage)
    {
        int[] pages = string.IsNullOrWhiteSpace(source)
            ? []
            : Azure.AI.ContentUnderstanding.DocumentSource.Parse(source)
                .Select(static item => item.PageNumber)
                .Distinct()
                .Order()
                .ToArray();
        return pages.Length > 0 ? pages : [fallbackPage];
    }

    private static DocumentPageReference[] PageReferences(IEnumerable<int> pages)
        => pages.Select(static page => new DocumentPageReference(page)).ToArray();

    private static void AddMarkdownSegments(
        string markdown,
        IEnumerable<ContentSpan>? pageSpans,
        IEnumerable<ContentSpan> excludedSpans,
        int pageNumber,
        List<(int Offset, DocumentNode Node, DocumentExtractionEvidence? Evidence)> entries)
    {
        foreach (ContentSpan pageSpan in pageSpans ?? [])
        {
            int cursor = pageSpan.Offset;
            int pageEnd = Math.Min(markdown.Length, pageSpan.Offset + pageSpan.Length);
            foreach (ContentSpan excluded in excludedSpans
                .Where(span => span.Offset < pageEnd && span.Offset + span.Length > cursor)
                .OrderBy(static span => span.Offset))
            {
                AddSegment(cursor, Math.Max(cursor, excluded.Offset));
                cursor = Math.Max(cursor, excluded.Offset + excluded.Length);
            }
            AddSegment(cursor, pageEnd);
        }

        void AddSegment(int start, int end)
        {
            if (start < 0 || end <= start || end > markdown.Length)
            {
                return;
            }

            string text = DocumentExtractionDemoExtensions.ProjectProviderMarkdown(markdown[start..end]);
            if (text.Length == 0)
            {
                return;
            }

            entries.Add((
                start,
                new DocumentText(
                    DocumentExtractionDemoExtensions.CreateNodeId(
                        "content-understanding", pageNumber, "markdown-text", entries.Count),
                    text,
                    pageReferences: [new(pageNumber)]),
                null));
        }
    }

    private static string SliceMarkdown(string markdown, IEnumerable<ContentSpan>? spans)
    {
        if (markdown.Length == 0 || spans is null)
        {
            return string.Empty;
        }

        return string.Concat(spans
            .OrderBy(static span => span.Offset)
            .Where(span => span.Offset >= 0 && span.Length > 0 && span.Offset + span.Length <= markdown.Length)
            .Select(span => markdown.Substring(span.Offset, span.Length)));
    }
}
