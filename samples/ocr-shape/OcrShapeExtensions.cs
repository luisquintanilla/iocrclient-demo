using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;

namespace DemoOcr;

public static class OcrShapeExtensions
{
    /// <summary>
    /// Selects the exact provider Markdown when it exists, otherwise uses text projected only from
    /// canonical elements. Use this for provider-output displays and text evals, not ingestion.
    /// </summary>
    public static string GetProviderMarkdownOrCanonicalText(this DocumentPage page)
        => page.Markdown ?? page.Text;

    /// <summary>
    /// Returns text projected only from canonical elements. Provider Markdown is deliberately ignored.
    /// </summary>
    public static string GetCanonicalElementText(this DocumentPage page) => page.Text;

    public static async IAsyncEnumerable<DocumentExtractionPageResult> StreamAsUpdates(
        Func<CancellationToken, Task<DocumentExtractionResult>> extract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DocumentExtractionResult result = await extract(cancellationToken).ConfigureAwait(false);
        int total = result.Pages.Count;
        int processed = 0;

        foreach (DocumentPage page in result.Pages)
        {
            processed++;
            yield return new DocumentExtractionPageResult(page)
            {
                TotalPages = total,
                RawRepresentation = processed == total ? result.RawRepresentation : null,
                AdditionalProperties = processed == total ? result.AdditionalProperties : null,
            };
        }
    }

    public static string? GetModelId(this DocumentExtractionResult result)
        => result.AdditionalProperties?.TryGetValue("modelId", out object? modelId) == true
            ? modelId as string
            : null;

    public static bool GetIncludeImages(this DocumentExtractionOptions? options)
        => options?.AdditionalProperties?.TryGetValue("includeImages", out object? includeImages) == true
            && includeImages is true;
}
