using System.Security.Cryptography;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

using SkiaSharp;

namespace DemoOcr.Tests;

public sealed class ProviderAdapterHelpersTests
{
    [Fact]
    public void CreatePageFrame_BindsReferenceToExactSourcePageIdentifier()
    {
        DocumentSourceOutput output = CreateOutput();

        (DocumentSourcePage page, DocumentPageReference reference) =
            ProviderAdapterHelpers.CreatePageFrame(
                "invoice-42",
                "provider-x",
                7,
                612,
                792,
                DocumentCoordinateUnit.Point,
                DocumentCoordinateOrigin.BottomLeft,
                output);

        Assert.Equal("invoice-42:provider-x:page:7", page.Identifier);
        Assert.Equal(page.Identifier, reference.Value);
        Assert.Equal(7, page.PageNumber);
        Assert.Same(output, page.SourceOutput);
    }

    [Fact]
    public void NormalizeOptions_ClonesCallerOptions()
    {
        var original = new DocumentExtractionOptions
        {
            ModelId = "caller-model",
            CaptureRawOutput = true,
        };
        original.AdditionalProperties["locale"] = "en-US";

        DocumentExtractionOptions clone = ProviderAdapterHelpers.NormalizeOptions(original);
        clone.ModelId = "changed";
        clone.AdditionalProperties["locale"] = "fr-FR";

        Assert.NotSame(original, clone);
        Assert.Equal("caller-model", original.ModelId);
        Assert.Equal("en-US", original.AdditionalProperties["locale"]);
        Assert.True(original.CaptureRawOutput);
    }

    [Fact]
    public void ResolveModel_UsesDefaultAllowsOverrideAndRejectsForbiddenOverride()
    {
        var options = new DocumentExtractionOptions();
        Assert.Equal("default", ProviderAdapterHelpers.ResolveModel(options, "default", "provider"));

        options.ModelId = "override";
        Assert.Equal("override", ProviderAdapterHelpers.ResolveModel(options, "default", "provider"));

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => ProviderAdapterHelpers.ResolveModel(
                options,
                "default",
                "provider",
                allowOverride: false));
        Assert.Contains("does not support per-request model overrides", error.Message);
        Assert.Contains("'default'", error.Message);
    }

    [Fact]
    public void RejectUnsupportedProperties_ReportsSortedUnsupportedNames()
    {
        var options = new DocumentExtractionOptions();
        options.AdditionalProperties["zeta"] = true;
        options.AdditionalProperties["allowed"] = 1;
        options.AdditionalProperties["Alpha"] = false;

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => ProviderAdapterHelpers.RejectUnsupportedProperties(
                options,
                "provider",
                "ALLOWED"));

        Assert.Equal(
            "provider does not support extraction option(s): Alpha, zeta.",
            error.Message);
    }

    [Fact]
    public void CreateSourceOutput_CaptureDisabledMarksDisabledAndDropsRepresentations()
    {
        object native = new();
        var options = new DocumentExtractionOptions { CaptureRawOutput = false };

        DocumentSourceOutput output = ProviderAdapterHelpers.CreateSourceOutput(
            "id", "provider", "model", options, native, [1, 2, 3]);

        Assert.Equal(DocumentRawContentStatus.Disabled, output.NativeStatus);
        Assert.Equal(DocumentRawContentStatus.Disabled, output.ContentStatus);
        Assert.Null(output.GetRawRepresentation());
        Assert.Null(output.GetRawContent());
    }

    [Fact]
    public void CreateSourceOutput_CaptureEnabledWithBytesRetainsNativeAndExactRawContent()
    {
        object native = new();
        byte[] bytes = [0, 1, 2, 255];
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentSourceOutput output = ProviderAdapterHelpers.CreateSourceOutput(
            "id", "provider", "model", options, native, bytes, "application/octet-stream");

        Assert.Equal(DocumentRawContentStatus.Retained, output.NativeStatus);
        Assert.Equal(DocumentRawContentStatus.Retained, output.ContentStatus);
        Assert.Same(native, output.GetRawRepresentation());
        DocumentArtifact artifact = Assert.IsType<DocumentArtifact>(output.GetRawContent());
        Assert.Equal("application/octet-stream", artifact.MediaType);
        using var copy = new MemoryStream();
        artifact.Content.CopyTo(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Fact]
    public void CreateSourceOutput_CaptureEnabledWithoutBytesDistinguishesUnavailableRawContent()
    {
        object native = new();
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentSourceOutput output = ProviderAdapterHelpers.CreateSourceOutput(
            "id", "provider", "model", options, native);

        Assert.Equal(DocumentRawContentStatus.Retained, output.NativeStatus);
        Assert.Equal(DocumentRawContentStatus.Unavailable, output.ContentStatus);
        Assert.Same(native, output.GetRawRepresentation());
        Assert.Null(output.GetRawContent());
    }

    [Fact]
    public void CreateSourceOutput_CaptureEnabledWithoutRepresentationsMarksBothUnavailable()
    {
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentSourceOutput output = ProviderAdapterHelpers.CreateSourceOutput(
            "id", "provider", "model", options, nativeRepresentation: null);

        Assert.Equal(DocumentRawContentStatus.Unavailable, output.NativeStatus);
        Assert.Equal(DocumentRawContentStatus.Unavailable, output.ContentStatus);
        Assert.Null(output.GetRawRepresentation());
        Assert.Null(output.GetRawContent());
    }

    private static DocumentSourceOutput CreateOutput()
        => ProviderAdapterHelpers.CreateSourceOutput(
            "id",
            "provider",
            "model",
            new DocumentExtractionOptions(),
            nativeRepresentation: null);
}

public sealed class RtDetrLayoutDetectorTests
{
    [Fact]
    public void Constructor_BlankAndMissingModelPathsReportExplicitPrerequisite()
    {
        ArgumentException blank = Assert.Throws<ArgumentException>(
            () => new RtDetrLayoutDetector(" "));
        Assert.Equal("modelPath", blank.ParamName);
        Assert.Contains("model path is required", blank.Message, StringComparison.OrdinalIgnoreCase);

        string missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        FileNotFoundException missing = Assert.Throws<FileNotFoundException>(
            () => new RtDetrLayoutDetector(missingPath));
        Assert.Equal(missingPath, missing.FileName);
        Assert.Contains("required for explicit layoutMode 'rtdetr'", missing.Message);
    }

    [Fact]
    public void Constructor_BadModelHashIsRejectedBeforeOnnxLoad()
    {
        string modelPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(modelPath, [1, 2, 3, 4]);
        try
        {
            string actual = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3, 4]));
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => new RtDetrLayoutDetector(modelPath));

            Assert.Contains(RtDetrLayoutDetector.PublishedSha256, error.Message);
            Assert.Contains(actual, error.Message);
            Assert.Contains("SHA-256 mismatch", error.Message);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }

    [RtDetrModelFact]
    public async Task Detect_NonSquareRenderedPageAlignsWithNativeTextRegion()
    {
        string modelPath = Environment.GetEnvironmentVariable("OCR_SHAPE_RTDETR_MODEL")!;
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("A real CPU layout inference title", Width: 300, Height: 400));
        using var detector = new RtDetrLayoutDetector(modelPath);
        var rasterizer = new PdfPageRasterizer(144);
        using var client = new PdfPigDocumentExtractionClient(
            rasterizer: rasterizer,
            layoutDetector: detector);
        var options = new DocumentExtractionOptions();
        options.AdditionalProperties["layoutMode"] = "rtdetr";

        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage extractedPage in client.ExtractPagesAsync(
            new MemoryStream(pdf, writable: false),
            "geometry.pdf",
            "application/pdf",
            options))
        {
            pages.Add(extractedPage);
        }

        DocumentExtractionPage page = Assert.Single(pages);
        DocumentRegion nativeRegion = Assert.Single(
            Assert.Single(page.Observations).Annotations
                .Single(annotation => annotation.Kind == "native-text-block")
                .Regions);
        DocumentRegion[] detectionRegions = page.Annotations
            .Where(annotation => annotation.Kind == "layout-detection")
            .SelectMany(annotation => annotation.Regions)
            .ToArray();

        double bestOverlap = detectionRegions
            .Select(region => IntersectionOverUnion(nativeRegion, region))
            .DefaultIfEmpty()
            .Max();

        Assert.NotEmpty(detectionRegions);
        Assert.True(
            bestOverlap > 0.5,
            $"Expected a detection aligned with the known text region; best IoU was {bestOverlap:F6}.");
    }

    private static double IntersectionOverUnion(DocumentRegion left, DocumentRegion right)
    {
        double leftX = left.Polygon.Min(point => point.X);
        double leftBottom = left.Polygon.Min(point => point.Y);
        double leftRight = left.Polygon.Max(point => point.X);
        double leftTop = left.Polygon.Max(point => point.Y);
        double rightX = right.Polygon.Min(point => point.X);
        double rightBottom = right.Polygon.Min(point => point.Y);
        double rightRight = right.Polygon.Max(point => point.X);
        double rightTop = right.Polygon.Max(point => point.Y);
        double intersectionWidth = Math.Max(
            0,
            Math.Min(leftRight, rightRight) - Math.Max(leftX, rightX));
        double intersectionHeight = Math.Max(
            0,
            Math.Min(leftTop, rightTop) - Math.Max(leftBottom, rightBottom));
        double intersection = intersectionWidth * intersectionHeight;
        double union = (leftRight - leftX) * (leftTop - leftBottom)
            + (rightRight - rightX) * (rightTop - rightBottom)
            - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}

internal sealed class RtDetrModelFactAttribute : FactAttribute
{
    public RtDetrModelFactAttribute()
    {
        string? modelPath = Environment.GetEnvironmentVariable("OCR_SHAPE_RTDETR_MODEL");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            Skip = "Set OCR_SHAPE_RTDETR_MODEL to the pinned ONNX model path.";
        }
    }
}

public sealed class PdfPageRasterizerTests
{
    [Fact]
    public void RenderPage_NormalTextPageUsesRealPdfiumAndExactTransform()
    {
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage("Rasterized text", Width: 300, Height: 400));
        var rasterizer = new PdfPageRasterizer(72);

        PdfRasterizedPage rendered = rasterizer.RenderPage(
            pdf, 1, 300, 400, 0, 0, 0, 300, 400);

        Assert.Equal(1, rendered.PageNumber);
        Assert.Equal(300, rendered.Width);
        Assert.Equal(400, rendered.Height);
        Assert.Equal(72, rendered.Transform.Dpi);
        PdfPointBox mapped = rendered.Transform.ToSourceBox(new PixelBox(30, 40, 90, 120));
        Assert.Equal(new PdfPointBox(30, 280, 90, 360), mapped);
        using SKBitmap bitmap = Assert.IsType<SKBitmap>(SKBitmap.Decode(rendered.PngBytes));
        Assert.Equal(300, bitmap.Width);
        Assert.Equal(400, bitmap.Height);
        Assert.Contains(bitmap.Pixels, pixel => pixel != SKColors.White);
    }

    [Fact]
    public void RenderPage_ImageOnlyPageUsesRealPdfiumAndProducesNonBlankPng()
    {
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage(ImageOnly: true, Width: 200, Height: 250));

        PdfRasterizedPage rendered = new PdfPageRasterizer(72).RenderPage(
            pdf, 1, 200, 250, 0, 0, 0, 200, 250);

        using SKBitmap bitmap = Assert.IsType<SKBitmap>(SKBitmap.Decode(rendered.PngBytes));
        Assert.Equal((200, 250), (rendered.Width, rendered.Height));
        Assert.Contains(bitmap.Pixels, pixel => pixel.Red > 200 && pixel.Green < 80 && pixel.Blue < 80);
        Assert.Equal(new PdfPointBox(0, 0, 200, 250),
            rendered.Transform.ToSourceBox(new PixelBox(0, 0, 200, 250)));
    }

    [Theory]
    [InlineData(90, 0, 0, 300, 400)]
    [InlineData(0, 10, 0, 300, 400)]
    [InlineData(0, 0, 5, 300, 400)]
    [InlineData(0, 0, 0, 290, 400)]
    [InlineData(0, 0, 0, 300, 390)]
    public void RenderPage_RotatedOrOffsetCropTransformsAreRejected(
        int rotation,
        double left,
        double bottom,
        double right,
        double top)
    {
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage("text", Width: 300, Height: 400));

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => new PdfPageRasterizer(72).RenderPage(
                pdf, 1, 300, 400, rotation, left, bottom, right, top));

        Assert.Contains("requires an unrotated PDF page", error.Message);
        Assert.Contains($"rotation={rotation}", error.Message);
        Assert.Contains("Native PdfPig extraction remains available", error.Message);
    }
}
