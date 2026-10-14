using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.Util;

namespace DemoOcr;

public enum OcrPolicy
{
    Never,
    FallbackForEmptyPages,
    AllPages,
}

public readonly record struct PageOcrContext(int PageNumber, int NativeElementCount)
{
    public bool HasNativeText => NativeElementCount > 0;
}

/// <summary>
/// MEDI reader that finalizes the shared document produced by an extraction client.
/// </summary>
public sealed class PdfPigReader(
    IDocumentExtractionClient? ocrClient = null,
    OcrPolicy policy = OcrPolicy.Never,
    IPageSegmenter? pageSegmenter = null,
    Func<PageOcrContext, bool>? ocrPagePredicate = null,
    DocumentExtractionOptions? options = null,
    IWordExtractor? wordExtractor = null) : IngestionDocumentReader
{
    public int OcrCalls { get; private set; }

    public override async Task<Document> ReadAsync(
        Stream source,
        string identifier,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        IDocumentExtractionClient client = policy switch
        {
            OcrPolicy.Never => new PdfPigDocumentExtractionClient(pageSegmenter, wordExtractor),
            OcrPolicy.AllPages when ocrClient is not null => ocrClient,
            OcrPolicy.AllPages => throw new ArgumentNullException(
                nameof(ocrClient),
                $"An {nameof(IDocumentExtractionClient)} is required when policy is {policy}."),
            OcrPolicy.FallbackForEmptyPages when ocrClient is not null =>
                new FallbackPdfPigExtractionClient(
                    new PdfPigDocumentExtractionClient(pageSegmenter, wordExtractor),
                    ocrClient,
                    ocrPagePredicate,
                    () => OcrCalls++),
            OcrPolicy.FallbackForEmptyPages => throw new ArgumentNullException(
                nameof(ocrClient),
                $"An {nameof(IDocumentExtractionClient)} is required when policy is {policy}."),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };

        bool ownsClient = policy is OcrPolicy.Never or OcrPolicy.FallbackForEmptyPages;
        try
        {
            var reader = new DocumentExtractionReader(client, options);
            Document document = await reader
                .ReadAsync(source, identifier, mediaType, cancellationToken)
                .ConfigureAwait(false);
            if (policy == OcrPolicy.AllPages)
            {
                OcrCalls++;
            }

            return document;
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    private sealed class FallbackPdfPigExtractionClient(
        PdfPigDocumentExtractionClient nativeClient,
        IDocumentExtractionClient fallbackClient,
        Func<PageOcrContext, bool>? predicate,
        Action onFallback) : IDocumentExtractionClient
    {
        public async IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
            Stream document,
            string identifier,
            string mediaType,
            DocumentExtractionOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            var nativeOptions = new DocumentExtractionOptions
            {
                CaptureRawOutput = options?.CaptureRawOutput ?? false,
            };
            var nativePages = new List<DocumentExtractionPage>();
            await foreach (DocumentExtractionPage page in nativeClient.ExtractPagesAsync(
                new MemoryStream(bytes, writable: false),
                identifier,
                mediaType,
                nativeOptions,
                cancellationToken).ConfigureAwait(false))
            {
                nativePages.Add(page);
            }

            int[] requestedPages = nativePages
                .Where(page => (predicate ?? (static context => !context.HasNativeText))(
                    new PageOcrContext(page.PageNumber, page.Observations.Count)))
                .Select(page => page.PageNumber)
                .ToArray();
            if (requestedPages.Length == 0)
            {
                foreach (DocumentExtractionPage page in nativePages)
                {
                    yield return page;
                }

                yield break;
            }

            onFallback();
            var fallbackPages = new Dictionary<int, DocumentExtractionPage>();
            await foreach (DocumentExtractionPage page in fallbackClient.ExtractPagesAsync(
                new MemoryStream(bytes, writable: false),
                identifier,
                mediaType,
                options,
                cancellationToken).ConfigureAwait(false))
            {
                if (!fallbackPages.TryAdd(page.PageNumber, page))
                {
                    throw new InvalidDataException(
                        $"The fallback extraction client returned duplicate page {page.PageNumber}.");
                }
            }

            var requestedSet = requestedPages.ToHashSet();
            foreach (DocumentExtractionPage nativePage in nativePages)
            {
                if (!requestedSet.Contains(nativePage.PageNumber))
                {
                    yield return nativePage;
                    continue;
                }

                if (!fallbackPages.TryGetValue(
                    nativePage.PageNumber,
                    out DocumentExtractionPage? fallbackPage))
                {
                    throw new InvalidDataException(
                        $"The fallback extraction client did not return requested page {nativePage.PageNumber}. " +
                        "The reader will not fabricate or renumber pages.");
                }

                yield return fallbackPage;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() => nativeClient.Dispose();
    }
}
