using System.Runtime.InteropServices;

using Docnet.Core;
using Docnet.Core.Models;

using Microsoft.Extensions.Documents;

using SkiaSharp;

namespace DemoOcr;

/// <summary>
/// Renders PDF pages with PDFium. <see cref="DocLib.Instance"/> remains application-owned.
/// </summary>
public sealed class PdfPageRasterizer(double dpi = 144)
{
    public double Dpi { get; } = dpi > 0
        ? dpi
        : throw new ArgumentOutOfRangeException(nameof(dpi));

    public PdfRasterizedPage RenderPage(
        byte[] pdfBytes,
        int pageNumber,
        double sourceWidthPoints,
        double sourceHeightPoints,
        int rotationDegrees,
        double cropLeft,
        double cropBottom,
        double cropRight,
        double cropTop)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        ValidateExactTransform(
            sourceWidthPoints,
            sourceHeightPoints,
            rotationDegrees,
            cropLeft,
            cropBottom,
            cropRight,
            cropTop);

        using var documentReader = DocLib.Instance.GetDocReader(
            pdfBytes,
            new PageDimensions(Dpi / 72d));
        if (pageNumber > documentReader.GetPageCount())
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber),
                $"PDF contains {documentReader.GetPageCount()} pages.");
        }

        using var pageReader = documentReader.GetPageReader(pageNumber - 1);
        byte[] bgra = pageReader.GetImage();
        int width = pageReader.GetPageWidth();
        int height = pageReader.GetPageHeight();
        int expectedLength = checked(width * height * 4);
        if (bgra.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"PDFium returned {bgra.Length} bytes for a {width}x{height} BGRA image; " +
                $"expected {expectedLength}.");
        }

        var imageInfo = new SKImageInfo(
            width,
            height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var bitmap = new SKBitmap(imageInfo);
        Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("SkiaSharp failed to encode the rendered PDF page.");
        var transform = new PdfRasterTransform(
            width,
            height,
            sourceWidthPoints,
            sourceHeightPoints,
            Dpi,
            rotationDegrees,
            cropLeft,
            cropBottom,
            cropRight,
            cropTop);
        return new PdfRasterizedPage(
            pageNumber,
            encoded.ToArray(),
            width,
            height,
            transform);
    }

    private static void ValidateExactTransform(
        double sourceWidthPoints,
        double sourceHeightPoints,
        int rotationDegrees,
        double cropLeft,
        double cropBottom,
        double cropRight,
        double cropTop)
    {
        const double tolerance = 0.01;
        bool supported = rotationDegrees == 0
            && Math.Abs(cropLeft) <= tolerance
            && Math.Abs(cropBottom) <= tolerance
            && Math.Abs(cropRight - sourceWidthPoints) <= tolerance
            && Math.Abs(cropTop - sourceHeightPoints) <= tolerance;
        if (!supported)
        {
            throw new NotSupportedException(
                "RT-DETR overlay requires an unrotated PDF page whose crop box origin is (0,0) " +
                "and whose crop box dimensions match the PdfPig source frame. " +
                $"Received rotation={rotationDegrees}, crop=[{cropLeft},{cropBottom},{cropRight},{cropTop}], " +
                $"source={sourceWidthPoints}x{sourceHeightPoints}. " +
                "Native PdfPig extraction remains available with layoutMode 'native'.");
        }
    }
}

public sealed record PdfRasterizedPage(
    int PageNumber,
    byte[] PngBytes,
    int Width,
    int Height,
    PdfRasterTransform Transform);

public sealed record PdfRasterTransform(
    int RasterWidth,
    int RasterHeight,
    double SourceWidthPoints,
    double SourceHeightPoints,
    double Dpi,
    int RotationDegrees,
    double CropLeft,
    double CropBottom,
    double CropRight,
    double CropTop)
{
    public PdfPointBox ToSourceBox(PixelBox box)
        => new(
            box.Left / RasterWidth * SourceWidthPoints,
            SourceHeightPoints - (box.Bottom / RasterHeight * SourceHeightPoints),
            box.Right / RasterWidth * SourceWidthPoints,
            SourceHeightPoints - (box.Top / RasterHeight * SourceHeightPoints));

    public DocumentRegion ToSourceRegion(DocumentSourcePage sourcePage, PixelBox box)
    {
        PdfPointBox mapped = ToSourceBox(box);
        return new DocumentRegion(
            sourcePage,
            [
                new DocumentPoint(mapped.Left, mapped.Bottom),
                new DocumentPoint(mapped.Right, mapped.Bottom),
                new DocumentPoint(mapped.Right, mapped.Top),
                new DocumentPoint(mapped.Left, mapped.Top),
            ]);
    }
}

public readonly record struct PixelBox(double Left, double Top, double Right, double Bottom);

public readonly record struct PdfPointBox(double Left, double Bottom, double Right, double Top);
