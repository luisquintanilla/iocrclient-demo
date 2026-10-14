using System.Runtime.CompilerServices;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Util;

namespace DemoOcr.Tests;

public sealed class PdfPigDocumentExtractionClientTests
{
    [Fact]
    public async Task ExtractPages_BindsEveryReferenceToItsExactSourcePageIdentifier()
    {
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("first"),
            new PdfTestPage("second"));
        using var client = new PdfPigDocumentExtractionClient();

        DocumentExtractionPage[] pages = await CollectAsync(client, pdf, "binding.pdf");

        Assert.Equal(2, pages.Length);
        Assert.Collection(
            pages,
            page =>
            {
                Assert.Equal("binding.pdf:pdfpig:page:1", page.SourcePage!.Identifier);
                Assert.Equal(page.SourcePage.Identifier, Assert.Single(page.PageReferences).Value);
                Assert.All(page.Observations, observation =>
                    Assert.Equal(
                        page.SourcePage.Identifier,
                        Assert.Single(observation.PageReferences).Value));
            },
            page =>
            {
                Assert.Equal("binding.pdf:pdfpig:page:2", page.SourcePage!.Identifier);
                Assert.Equal(page.SourcePage.Identifier, Assert.Single(page.PageReferences).Value);
                Assert.All(page.Observations, observation =>
                    Assert.Equal(
                        page.SourcePage.Identifier,
                        Assert.Single(observation.PageReferences).Value));
            });
    }

    [Fact]
    public async Task ExtractPages_PreservesBlankPageBetweenTextPages()
    {
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("alpha"),
            new PdfTestPage(),
            new PdfTestPage("omega"));
        using var client = new PdfPigDocumentExtractionClient();

        DocumentExtractionPage[] pages = await CollectAsync(client, pdf, "three-pages.pdf");

        Assert.Equal([1, 2, 3], pages.Select(page => page.PageNumber));
        Assert.Contains("alpha", pages[0].Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(pages[1].Observations);
        Assert.Equal(string.Empty, pages[1].Markdown);
        Assert.Equal("three-pages.pdf:pdfpig:page:2", pages[1].SourcePage!.Identifier);
        Assert.Contains("omega", pages[2].Markdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractPages_UsesPointBottomLeftFrameAndRealBlockPolygon()
    {
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("Polygon proof", Width: 300, Height: 400));
        using var client = new PdfPigDocumentExtractionClient();

        DocumentExtractionPage page = Assert.Single(
            await CollectAsync(client, pdf, "geometry.pdf"));
        DocumentExtractionText text = Assert.IsType<DocumentExtractionText>(
            Assert.Single(page.Observations));
        DocumentAnnotation annotation = Assert.Single(
            text.Annotations,
            item => item.Kind == "native-text-block");
        DocumentRegion region = Assert.Single(annotation.Regions);

        Assert.Equal(DocumentCoordinateUnit.Point, page.SourcePage!.CoordinateUnit);
        Assert.Equal(DocumentCoordinateOrigin.BottomLeft, page.SourcePage.CoordinateOrigin);
        Assert.Equal(300, page.SourcePage.Dimensions?.Width);
        Assert.Equal(400, page.SourcePage.Dimensions?.Height);
        Assert.Same(page.SourcePage, region.Page);
        Assert.Equal(4, region.Polygon.Count);
        Assert.True(region.Polygon[0].X < region.Polygon[1].X);
        Assert.Equal(region.Polygon[0].Y, region.Polygon[1].Y, precision: 6);
        Assert.True(region.Polygon[2].Y > region.Polygon[1].Y);
        Assert.Equal(region.Polygon[2].Y, region.Polygon[3].Y, precision: 6);
    }

    [Fact]
    public async Task ExtractPages_RtDetrModeRequiresRasterizerAndDetector()
    {
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage("layout"));
        using var client = new PdfPigDocumentExtractionClient();
        var options = new DocumentExtractionOptions();
        options.AdditionalProperties["layoutMode"] = "rtdetr";

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CollectAsync(client, pdf, "layout.pdf", options));

        Assert.Equal(
            "layoutMode 'rtdetr' requires both PdfPageRasterizer and RtDetrLayoutDetector.",
            error.Message);
    }

    [Fact]
    public async Task ExtractPages_NativeLayoutNeverFabricatesTableCells()
    {
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("A | B\n1 | 2"));
        using var client = new PdfPigDocumentExtractionClient();

        DocumentExtractionPage page = Assert.Single(
            await CollectAsync(client, pdf, "looks-like-table.pdf"));

        Assert.NotEmpty(page.Observations);
        Assert.All(page.Observations, observation => Assert.IsType<DocumentExtractionText>(observation));
        Assert.DoesNotContain(page.Observations, observation => observation is DocumentExtractionTable);
        Assert.DoesNotContain(
            page.Observations.SelectMany(observation => observation.Annotations),
            annotation => annotation.Kind == "table-cell");
    }

    [Theory]
    [InlineData("unsupported", null)]
    [InlineData("native", 1.01f)]
    [InlineData("native", -0.01f)]
    public async Task ExtractPages_InvalidLayoutOptionsAreRejected(
        string layoutMode,
        float? threshold)
    {
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage("layout"));
        using var client = new PdfPigDocumentExtractionClient();
        var options = new DocumentExtractionOptions();
        options.AdditionalProperties["layoutMode"] = layoutMode;
        if (threshold.HasValue)
        {
            options.AdditionalProperties["layoutConfidenceThreshold"] = threshold.Value;
        }

        Exception error = await Assert.ThrowsAnyAsync<Exception>(
            async () => await CollectAsync(client, pdf, "invalid-layout.pdf", options));

        if (layoutMode == "unsupported")
        {
            NotSupportedException unsupported = Assert.IsType<NotSupportedException>(error);
            Assert.Contains("Use 'native' or 'rtdetr'", unsupported.Message);
        }
        else
        {
            ArgumentOutOfRangeException range = Assert.IsType<ArgumentOutOfRangeException>(error);
            Assert.Equal("options", range.ParamName);
            Assert.Contains("must be in [0,1]", range.Message);
        }
    }

    private static async Task<DocumentExtractionPage[]> CollectAsync(
        PdfPigDocumentExtractionClient client,
        byte[] pdf,
        string identifier,
        DocumentExtractionOptions? options = null)
    {
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            new MemoryStream(pdf, writable: false),
            identifier,
            "application/pdf",
            options))
        {
            pages.Add(page);
        }

        return pages.ToArray();
    }
}

public sealed class PdfPigReaderTests
{
    [Fact]
    public async Task ReadAsync_NeverPolicyPreservesPdfPigPagesAndIdentifiers()
    {
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("one"),
            new PdfTestPage(),
            new PdfTestPage("three"));
        var reader = new PdfPigReader(policy: OcrPolicy.Never);

        Document document = await reader.ReadAsync(
            new MemoryStream(pdf, writable: false),
            "reader.pdf",
            "application/pdf");

        Assert.Equal(3, document.SourcePages.Count);
        Assert.Equal(
            [
                "reader.pdf:pdfpig:page:1",
                "reader.pdf:pdfpig:page:2",
                "reader.pdf:pdfpig:page:3",
            ],
            document.SourcePages.Select(page => page.Identifier));
        Assert.Equal(0, reader.OcrCalls);
    }

    [Fact]
    public async Task ReadAsync_AllPagesUsesExplicitClientAndCountsOneCall()
    {
        using var fake = new FakeExtractionClient();
        var reader = new PdfPigReader(fake, OcrPolicy.AllPages);

        Document document = await reader.ReadAsync(
            new MemoryStream([9, 8, 7], writable: false),
            "explicit.bin",
            "application/octet-stream");

        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, reader.OcrCalls);
        Assert.Equal("explicit.bin:fake:page:4", Assert.Single(document.SourcePages).Identifier);
        Assert.Contains("fake markdown", document.StoredMarkdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_FallbackPolicyReplacesOnlyRequestedBlankPage()
    {
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("one"),
            new PdfTestPage(),
            new PdfTestPage("three"));
        using var fake = new FakeExtractionClient(2);
        var reader = new PdfPigReader(fake, OcrPolicy.FallbackForEmptyPages);

        Document document = await reader.ReadAsync(
            new MemoryStream(pdf, writable: false),
            "fallback.pdf",
            "application/pdf");

        Assert.Equal(1, fake.Calls);
        Assert.Equal(1, reader.OcrCalls);
        Assert.Equal(
            [
                "fallback.pdf:pdfpig:page:1",
                "fallback.pdf:fake:page:2",
                "fallback.pdf:pdfpig:page:3",
            ],
            document.SourcePages.Select(page => page.Identifier));
    }

    [Fact]
    public async Task ReadAsync_NeverPolicyUsesCallerProvidedWordExtractor()
    {
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage("custom words"));
        var extractor = new CountingWordExtractor();
        var reader = new PdfPigReader(
            policy: OcrPolicy.Never,
            wordExtractor: extractor);

        await reader.ReadAsync(
            new MemoryStream(pdf, writable: false),
            "words.pdf",
            "application/pdf");

        Assert.Equal(1, extractor.Calls);
    }

    private sealed class FakeExtractionClient(int pageNumber = 4) : IDocumentExtractionClient
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
            Stream document,
            string identifier,
            string mediaType,
            DocumentExtractionOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            DocumentSourceOutput output = ProviderAdapterHelpers.CreateSourceOutput(
                identifier,
                "fake",
                "fixed",
                options ?? new DocumentExtractionOptions(),
                nativeRepresentation: null);
            (DocumentSourcePage page, DocumentPageReference reference) =
                ProviderAdapterHelpers.CreatePageFrame(
                    identifier,
                    "fake",
                    pageNumber,
                    null,
                    null,
                    null,
                    null,
                    output);
            yield return new DocumentExtractionPage(
                pageNumber,
                [
                    new DocumentExtractionText(
                        "fake:text",
                        0,
                        DocumentTextKind.Paragraph,
                        "fake markdown",
                        "fake markdown",
                        pageReferences: [reference]),
                ],
                "fake markdown",
                [reference],
                page,
                sourceOutputs: [output]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CountingWordExtractor : IWordExtractor
    {
        public int Calls { get; private set; }

        public IEnumerable<Word> GetWords(IReadOnlyList<Letter> letters)
        {
            Calls++;
            return NearestNeighbourWordExtractor.Instance.GetWords(letters);
        }
    }
}
