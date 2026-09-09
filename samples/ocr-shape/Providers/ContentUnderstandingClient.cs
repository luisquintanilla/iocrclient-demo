using Azure;
using Azure.AI.ContentUnderstanding;
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
/// Azure AI Content Understanding behind the SAME <see cref="IDocumentExtractionClient"/> contract (the markdown path).
///
/// CU is the widest-surface PEER in the provider matrix, not an apex: one CU service can emit EITHER
/// Markdown (this client, <see cref="IDocumentExtractionClient"/>) OR typed fields + grounding + confidence
/// (<see cref="ContentUnderstandingAnalysisClient"/>, the sibling <see cref="IDocumentAnalysisClient"/>).
/// CU exercises the same polygon, confidence, and builder primitives as Mistral OCR, Azure DI, and a
/// vision LLM. It is a peer behind the contract, never privileged over the others.
///
/// Wire protocol: analyzer + async-poll (<c>AnalyzeBinary(WaitUntil.Completed, analyzerId, …)</c> →
/// <c>AnalysisResult.Contents[]</c>). Different from Mistral (document→pages[]) and DI (AnalyzeResult),
/// so it is a different class, but it normalizes onto the same <see cref="DocumentExtractionResult"/>.
/// CU can return full provider Markdown plus a partial canonical element set. Direct displays select
/// exact Markdown deliberately. The explicit bridge gives canonical elements precedence, so this
/// provider's mixed representation is not a validated bridge path in this comparison. Keyless via
/// DefaultAzureCredential / any TokenCredential on a Foundry resource.
/// </summary>
public sealed class ContentUnderstandingClient : IDocumentExtractionClient
{
    /// <summary>The prebuilt analyzer that returns layout markdown (the RAG/reader path).</summary>
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
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        string analyzerId = options?.ModelId ?? _analyzerId;
        Operation<AnalysisResult> op = await _client
            .AnalyzeBinaryAsync(
                WaitUntil.Completed,
                analyzerId,
                BinaryData.FromBytes(ms.ToArray()),
                contentRange: null,
                contentType: mediaType,
                processingLocation: null,
                cancellationToken)
            .ConfigureAwait(false);

        AnalysisResult result = op.Value;
        var pages = new List<DocumentPage>();

        // CU returns one or more content segments; the document path yields DocumentContent with markdown.
        foreach (AnalysisContent content in result.Contents)
        {
            if (content is not DocumentContent doc)
            {
                continue;
            }

            var tablesByPage = new Dictionary<int, List<DocumentTable>>();
            if (doc.Tables is { Count: > 0 })
            {
                foreach (Azure.AI.ContentUnderstanding.DocumentTable t in doc.Tables)
                {
                    var cells = new List<DocumentTableCell>(t.Cells.Count);
                    foreach (Azure.AI.ContentUnderstanding.DocumentTableCell c in t.Cells)
                    {
                        cells.Add(new DocumentTableCell(
                            c.RowIndex,
                            c.ColumnIndex,
                            [new DocumentBlock(c.Content ?? "")])
                        {
                            Kind = c.Kind?.ToString() is { Length: > 0 } cellKind ? new DocumentTableCellKind(cellKind) : null,
                            RowSpan = c.RowSpan ?? 1,
                            ColumnSpan = c.ColumnSpan ?? 1,
                        });
                    }
                    // CU encodes geometry as a source string (not a polygon array); it rides in RawRepresentation.
                    (tablesByPage.TryGetValue(doc.StartPageNumber, out var list) ? list : tablesByPage[doc.StartPageNumber] = new())
                        .Add(new DocumentTable(t.RowCount, t.ColumnCount, cells));
                }
            }

            if (doc.Pages is { Count: > 0 })
            {
                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    Azure.AI.ContentUnderstanding.DocumentPage page = doc.Pages[i];
                    bool first = pages.Count == 0;
                    IReadOnlyList<DocumentElement> elements = tablesByPage.TryGetValue(page.PageNumber, out var tb)
                        ? tb.Cast<DocumentElement>().ToList()
                        : [];
                    pages.Add(new DocumentPage(
                        page.PageNumber,
                        elements,
                        first ? doc.Markdown ?? "" : "")
                    {
                        AdditionalProperties = new() { ["cu.pageNumber"] = page.PageNumber },
                    });
                }
            }
            else
            {
                pages.Add(new DocumentPage(1, [], doc.Markdown ?? ""));
            }
        }

        if (pages.Count == 0)
        {
            pages.Add(new DocumentPage(1, []));
        }

        return new DocumentExtractionResult(pages)
        {
            RawRepresentation = result,
            AdditionalProperties = new() { ["modelId"] = analyzerId },
        };
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(Azure.AI.ContentUnderstanding.ContentUnderstandingClient) ? _client : null;

    public void Dispose() { }
}
