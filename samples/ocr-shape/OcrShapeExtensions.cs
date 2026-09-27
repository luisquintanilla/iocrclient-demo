using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

/// <summary>
/// Small consumer-side helpers for the provisional streaming extraction contract.
/// </summary>
public static class OcrShapeExtensions
{
    /// <summary>
    /// Keeps the samples' short call shape while supplying the identifier required by the
    /// provisional Documents contract.
    /// </summary>
    public static Task<DocumentExtractionResult> ExtractAsync(
        this IDocumentExtractionClient client,
        Stream document,
        string mediaType,
        CancellationToken cancellationToken = default)
        => DocumentExtractionClientExtensions.ExtractAsync(
            client,
            document,
            "consumer-proof",
            mediaType,
            cancellationToken);

    public static async IAsyncEnumerable<DocumentExtractionPage> StreamPages(
        Func<CancellationToken, Task<IReadOnlyList<DocumentExtractionPage>>> extract,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DocumentExtractionPage> pages = await extract(cancellationToken).ConfigureAwait(false);
        foreach (DocumentExtractionPage page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return page;
        }
    }

    public static string GetMarkdown(this DocumentExtractionPage page)
        => page.Markdown
            ?? string.Join(
                Environment.NewLine,
                page.Observations
                    .OfType<DocumentExtractionText>()
                    .Select(static observation => observation.Text ?? observation.Markdown));

    public static string GetMarkdown(this DocumentExtractionResult result)
        => result.Document.ToMarkdown();

    public static IEnumerable<DocumentElement> EnumerateElements(this DocumentExtractionResult result)
        => result.Document.Sections.SelectMany(static section => section.Elements);

    public static int CountElements<TElement>(this DocumentExtractionResult result)
        where TElement : DocumentElement
        => result.EnumerateElements().OfType<TElement>().Count();
}
