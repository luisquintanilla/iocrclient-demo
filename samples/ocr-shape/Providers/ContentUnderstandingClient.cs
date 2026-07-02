using Azure;
using Azure.AI.ContentUnderstanding;
using Azure.Core;

using Microsoft.Extensions.AI;

namespace DemoOcr;

/// <summary>
/// Azure AI Content Understanding behind the SAME <see cref="IOcrClient"/> contract (the markdown path).
///
/// CU is the widest-surface PEER in the provider matrix, not an apex: one CU service can emit EITHER
/// Markdown (this client, <see cref="IOcrClient"/>) OR typed fields + grounding + confidence
/// (<see cref="ContentUnderstandingAnalysisClient"/>, the sibling <see cref="IDocumentAnalysisClient"/>).
/// That is exactly why CU is the strongest cross-provider conformance test — if the same polygon /
/// confidence / builder primitives serve CU's two shapes AND Mistral OCR AND Azure DI AND a vision LLM,
/// provider-neutrality is proven. But CU is a peer behind the contract, never privileged over the others.
///
/// Wire protocol: analyzer + async-poll (<c>AnalyzeBinary(WaitUntil.Completed, analyzerId, …)</c> →
/// <c>AnalysisResult.Contents[]</c>). Different from Mistral (document→pages[]) and DI (AnalyzeResult),
/// so it is a different class — but it normalizes onto the same <see cref="OcrResult"/>, so the
/// <see cref="OcrDocumentReader"/> and the vector store never see the difference. Keyless via
/// DefaultAzureCredential / any TokenCredential on a Foundry resource.
/// </summary>
public sealed class ContentUnderstandingClient : IOcrClient
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

    public async Task<OcrResult> ExtractAsync(
        Stream document,
        string mediaType,
        OcrOptions? options = null,
        IProgress<OcrProgress>? progress = null,
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
        var pages = new List<OcrPage>();

        // CU returns one or more content segments; the document path yields DocumentContent with markdown.
        foreach (AnalysisContent content in result.Contents)
        {
            if (content is not DocumentContent doc)
            {
                continue;
            }

            var tablesByPage = new Dictionary<int, List<OcrTable>>();
            if (doc.Tables is { Count: > 0 })
            {
                foreach (DocumentTable t in doc.Tables)
                {
                    var cells = new List<OcrTableCell>(t.Cells.Count);
                    foreach (DocumentTableCell c in t.Cells)
                    {
                        cells.Add(new OcrTableCell(c.RowIndex, c.ColumnIndex, c.Content ?? "")
                        {
                            Kind = c.Kind?.ToString(),
                            RowSpan = c.RowSpan ?? 1,
                            ColumnSpan = c.ColumnSpan ?? 1,
                        });
                    }
                    // CU encodes geometry as a source string (not a polygon array); it rides in RawRepresentation.
                    (tablesByPage.TryGetValue(doc.StartPageNumber, out var list) ? list : tablesByPage[doc.StartPageNumber] = new())
                        .Add(new OcrTable(t.RowCount, t.ColumnCount, cells));
                }
            }

            if (doc.Pages is { Count: > 0 })
            {
                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    DocumentPage page = doc.Pages[i];
                    bool first = pages.Count == 0;
                    pages.Add(new OcrPage(page.PageNumber - 1, first ? doc.Markdown ?? "" : "")
                    {
                        Tables = tablesByPage.TryGetValue(page.PageNumber, out var tb) ? tb : [],
                        AdditionalProperties = new() { ["cu.pageNumber"] = page.PageNumber },
                    });
                    progress?.Report(new OcrProgress
                    {
                        PagesProcessed = pages.Count,
                        TotalPages = doc.Pages.Count,
                        Status = "analyzing",
                    });
                }
            }
            else
            {
                pages.Add(new OcrPage(0, doc.Markdown ?? ""));
            }
        }

        if (pages.Count == 0)
        {
            pages.Add(new OcrPage(0, ""));
        }

        return new OcrResult(pages)
        {
            OcrSource = "azure-content-understanding",
            ModelId = analyzerId,
            RawRepresentation = result,
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(Azure.AI.ContentUnderstanding.ContentUnderstandingClient) ? _client : null;

    public void Dispose() { }
}
