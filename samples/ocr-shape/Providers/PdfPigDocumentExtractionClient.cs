using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.Util;

namespace DemoOcr;

/// <summary>
/// Extracts native PDF text with PdfPig. Optional RT-DETR enriches layout only and never performs OCR.
/// </summary>
public sealed class PdfPigDocumentExtractionClient(
    IPageSegmenter? pageSegmenter = null,
    IWordExtractor? wordExtractor = null,
    PdfPageRasterizer? rasterizer = null,
    RtDetrLayoutDetector? layoutDetector = null) : IDocumentExtractionClient
{
    private const string ProviderName = "pdfpig";

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => ExtractPagesCoreAsync(document, identifier, mediaType, options, cancellationToken);

    private async IAsyncEnumerable<DocumentExtractionPage> ExtractPagesCoreAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"{nameof(PdfPigDocumentExtractionClient)} only supports application/pdf.");
        }

        DocumentExtractionOptions normalizedOptions = ProviderAdapterHelpers.NormalizeOptions(options);
        ProviderAdapterHelpers.RejectUnsupportedProperties(
            normalizedOptions,
            nameof(PdfPigDocumentExtractionClient),
            "layoutMode",
            "layoutConfidenceThreshold");
        ProviderAdapterHelpers.ResolveModel(
            normalizedOptions,
            "pdfpig",
            nameof(PdfPigDocumentExtractionClient),
            allowOverride: false);
        string layoutMode =
            ProviderAdapterHelpers.GetProperty<string>(normalizedOptions, "layoutMode")
            ?? "native";
        if (layoutMode is not ("native" or "rtdetr"))
        {
            throw new NotSupportedException(
                $"PdfPig layoutMode '{layoutMode}' is unsupported. Use 'native' or 'rtdetr'.");
        }

        float confidenceThreshold =
            ProviderAdapterHelpers.GetProperty<float?>(normalizedOptions, "layoutConfidenceThreshold")
            ?? layoutDetector?.ConfidenceThreshold
            ?? 0.5f;
        if (confidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "layoutConfidenceThreshold must be in [0,1].");
        }

        if (layoutMode == "rtdetr" && (rasterizer is null || layoutDetector is null))
        {
            throw new InvalidOperationException(
                "layoutMode 'rtdetr' requires both PdfPageRasterizer and RtDetrLayoutDetector.");
        }

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        byte[] bytes = buffer.ToArray();
        DocumentSourceOutput sourceOutput = ProviderAdapterHelpers.CreateSourceOutput(
            identifier,
            ProviderName,
            "pdfpig",
            normalizedOptions,
            nativeRepresentation: null);
        using PdfDocument pdf = PdfDocument.Open(new MemoryStream(bytes, writable: false));
        IPageSegmenter segmenter = pageSegmenter ?? DefaultPageSegmenter.Instance;

        for (int pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Page page = pdf.GetPage(pageNumber);
            (DocumentSourcePage sourcePage, DocumentPageReference pageReference) =
                ProviderAdapterHelpers.CreatePageFrame(
                    identifier,
                    ProviderName,
                    pageNumber,
                    page.Width,
                    page.Height,
                    DocumentCoordinateUnit.Point,
                    DocumentCoordinateOrigin.BottomLeft,
                    sourceOutput);
            List<NativeBlock> blocks = segmenter
                .GetBlocks(wordExtractor is null ? page.GetWords() : page.GetWords(wordExtractor))
                .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                .Select((block, index) => new NativeBlock(
                    index,
                    block.Text,
                    block.BoundingBox.Left,
                    block.BoundingBox.Bottom,
                    block.BoundingBox.Right,
                    block.BoundingBox.Top))
                .ToList();

            IReadOnlyList<LayoutDetection> detections = [];
            PdfRasterTransform? rasterTransform = null;
            if (layoutMode == "rtdetr")
            {
                PdfRasterizedPage rendered = rasterizer!.RenderPage(
                    bytes,
                    pageNumber,
                    page.Width,
                    page.Height,
                    page.Rotation.Value,
                    page.CropBox.Bounds.Left,
                    page.CropBox.Bounds.Bottom,
                    page.CropBox.Bounds.Right,
                    page.CropBox.Bounds.Top);
                rasterTransform = rendered.Transform;
                detections = layoutDetector!.Detect(
                    rendered,
                    confidenceThreshold,
                    cancellationToken);
            }

            List<MappedBlock> mappedBlocks = blocks
                .Select(block => new MappedBlock(
                    block,
                    FindBestDetection(block, detections, rasterTransform)))
                .OrderBy(block => block.Detection?.Box.Top ?? double.MaxValue)
                .ThenByDescending(block => block.Block.Top)
                .ThenBy(block => block.Block.Left)
                .ToList();
            var observations = new List<DocumentExtractionObservation>(mappedBlocks.Count);
            foreach (MappedBlock mapped in mappedBlocks)
            {
                NativeBlock block = mapped.Block;
                var blockRegion = new DocumentRegion(
                    sourcePage,
                    [
                        new DocumentPoint(block.Left, block.Bottom),
                        new DocumentPoint(block.Right, block.Bottom),
                        new DocumentPoint(block.Right, block.Top),
                        new DocumentPoint(block.Left, block.Top),
                    ]);
                var annotations = new List<DocumentAnnotation>
                {
                    new(
                        "native-text-block",
                        regions: [blockRegion],
                        sourceOutput: sourceOutput),
                };
                if (mapped.Detection is { } detection && rasterTransform is not null)
                {
                    DocumentRegion detectionRegion = rasterTransform.ToSourceRegion(
                        sourcePage,
                        detection.Box);
                    annotations.Add(new DocumentAnnotation(
                        "layout-detection",
                        ProviderAdapterHelpers.StructuredValue(
                            ("labelId", detection.LabelId),
                            ("label", detection.Label)),
                        detection.Label,
                        [detectionRegion],
                        new DocumentConfidence(detection.Score, "model"),
                        sourceOutput));
                }

                observations.Add(new DocumentExtractionText(
                    $"pdfpig:page:{pageNumber}:block:{block.Index}",
                    observations.Count,
                    MapTextKind(mapped.Detection?.Label),
                    block.Text,
                    block.Text,
                    pageReferences: [pageReference],
                    annotations: annotations));
            }

            var pageAnnotations = new List<DocumentAnnotation>();
            if (rasterTransform is not null)
            {
                pageAnnotations.Add(new DocumentAnnotation(
                    "pdf-raster-transform",
                    rasterTransform,
                    role: "source-page-transform",
                    sourceOutput: sourceOutput));
                foreach (LayoutDetection detection in detections)
                {
                    pageAnnotations.Add(new DocumentAnnotation(
                        "layout-detection",
                        ProviderAdapterHelpers.StructuredValue(
                            ("labelId", detection.LabelId),
                            ("label", detection.Label)),
                        detection.Label,
                        [rasterTransform.ToSourceRegion(sourcePage, detection.Box)],
                        new DocumentConfidence(detection.Score, "model"),
                        sourceOutput));
                }
            }

            yield return new DocumentExtractionPage(
                pageNumber,
                observations,
                string.Join(Environment.NewLine + Environment.NewLine, observations.Select(item => item.Markdown)),
                [pageReference],
                sourcePage,
                pageAnnotations,
                [sourceOutput]);
        }
    }

    private static LayoutDetection? FindBestDetection(
        NativeBlock block,
        IReadOnlyList<LayoutDetection> detections,
        PdfRasterTransform? transform)
    {
        if (transform is null)
        {
            return null;
        }

        LayoutDetection? best = null;
        double bestOverlap = 0;
        foreach (LayoutDetection detection in detections)
        {
            PdfPointBox sourceBox = transform.ToSourceBox(detection.Box);
            double intersectionWidth = Math.Max(
                0,
                Math.Min(block.Right, sourceBox.Right) - Math.Max(block.Left, sourceBox.Left));
            double intersectionHeight = Math.Max(
                0,
                Math.Min(block.Top, sourceBox.Top) - Math.Max(block.Bottom, sourceBox.Bottom));
            double blockArea = Math.Max(0, block.Right - block.Left)
                * Math.Max(0, block.Top - block.Bottom);
            double overlap = blockArea == 0
                ? 0
                : intersectionWidth * intersectionHeight / blockArea;
            if (overlap > bestOverlap)
            {
                best = detection;
                bestOverlap = overlap;
            }
        }

        return bestOverlap > 0 ? best : null;
    }

    private static DocumentTextKind MapTextKind(string? label)
        => label is "Title" or "SectionHeader" or "PageHeader"
            ? DocumentTextKind.Header
            : DocumentTextKind.Paragraph;

    private sealed record NativeBlock(
        int Index,
        string Text,
        double Left,
        double Bottom,
        double Right,
        double Top);

    private sealed record MappedBlock(NativeBlock Block, LayoutDetection? Detection);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
