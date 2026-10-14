using Azure.AI.ContentUnderstanding;
using Azure.AI.DocumentIntelligence;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr.Tests;

public sealed class AzureDocumentIntelligenceMappingTests
{
    [Fact]
    public void BuildTableMarkdown_EmptyCellsRemainEmptyWithoutFabrication()
    {
        IReadOnlyList<DocumentExtractionTableCell> cells =
        [
            Cell(0, 1, "value"),
        ];

        string markdown = AzureDocumentIntelligenceClient.BuildTableMarkdown(2, 2, cells);

        Assert.Equal(
            $"|  | value |{Environment.NewLine}|  |  |",
            markdown);
        Assert.Single(cells);
        Assert.Equal((0, 1), (cells[0].Row, cells[0].Column));
    }

    [Fact]
    public void ExtractPageMarkdown_EmptyAndConcreteSpansAreHandledExactly()
    {
        Assert.Equal(
            string.Empty,
            AzureDocumentIntelligenceClient.ExtractPageMarkdown("012345", []));

        string extracted = AzureDocumentIntelligenceClient.ExtractPageMarkdown(
            "0123456789",
            [
                DocumentIntelligenceModelFactory.DocumentSpan(2, 2),
                DocumentIntelligenceModelFactory.DocumentSpan(6, 3),
            ]);

        Assert.Equal("2345678", extracted);
    }

    [Fact]
    public void RoleAndConfidenceMapping_PreserveAvailableValues()
    {
        Assert.Equal(
            DocumentTextKind.Header,
            AzureDocumentIntelligenceClient.ToTextKind(ParagraphRole.SectionHeading));
        Assert.Equal(
            DocumentTextKind.Paragraph,
            AzureDocumentIntelligenceClient.ToTextKind(ParagraphRole.Footnote));
        Assert.Equal(
            DocumentTextKind.Paragraph,
            AzureDocumentIntelligenceClient.ToTextKind(null));

        DocumentConfidence confidence = Assert.IsType<DocumentConfidence>(
            ProviderAdapterHelpers.CreateConfidence(0.73f));
        Assert.Equal(0.73, confidence.Value, precision: 6);
        Assert.Equal("provider", confidence.Kind);
        Assert.Null(ProviderAdapterHelpers.CreateConfidence(null));
    }

    private static DocumentExtractionTableCell Cell(int row, int column, string markdown)
    {
        var content = new DocumentExtractionText(
            $"cell:{row}:{column}",
            row * 2 + column,
            DocumentTextKind.Paragraph,
            markdown,
            markdown);
        return new DocumentExtractionTableCell(row, column, content);
    }
}

public sealed class ContentUnderstandingMappingTests
{
    [Fact]
    public void BuildTableMarkdown_EmptyCellsRemainEmptyWithoutFabrication()
    {
        IReadOnlyList<DocumentExtractionTableCell> cells =
        [
            Cell(1, 0, "only"),
        ];

        string markdown = ContentUnderstandingClient.BuildTableMarkdown(2, 2, cells);

        Assert.Equal(
            $"|  |  |{Environment.NewLine}| only |  |",
            markdown);
        Assert.Single(cells);
        Assert.Empty(cells[0].Annotations);
    }

    [Fact]
    public void ExtractPageMarkdown_EmptyAndConcreteSpansAreHandledExactly()
    {
        Assert.Equal(
            string.Empty,
            ContentUnderstandingClient.ExtractPageMarkdown("abcdef", []));

        string extracted = ContentUnderstandingClient.ExtractPageMarkdown(
            "abcdefghij",
            [
                ContentUnderstandingModelFactory.ContentSpan(1, 2),
                ContentUnderstandingModelFactory.ContentSpan(5, 2),
            ]);

        Assert.Equal("bcdefg", extracted);
    }

    [Fact]
    public void ExtractPageMarkdown_SpanPresentPathWinsOverPageBreakFallback()
    {
        string extracted = ContentUnderstandingClient.ExtractPageMarkdown(
            "first<!-- PageBreak -->second",
            [ContentUnderstandingModelFactory.ContentSpan(0, 5)],
            pageIndex: 0,
            pageCount: 2,
            contentHasPageSpans: true);

        Assert.Equal("first", extracted);
    }

    [Fact]
    public void ExtractPageMarkdown_SpanlessMarkerMismatchIsRejected()
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ContentUnderstandingClient.ExtractPageMarkdown(
                "first page without a marker",
                [],
                pageIndex: 0,
                pageCount: 2,
                contentHasPageSpans: false));

        Assert.Contains("expected 1 page-break markers", exception.Message);
    }

    [Fact]
    public void MissingSpanMapper_DoesNotCrashOrInventSpan()
    {
        object? missing = ContentUnderstandingClient.CreateSpanValue(null);
        object? present = ContentUnderstandingClient.CreateSpanValue(
            ContentUnderstandingModelFactory.ContentSpan(4, 7));

        Assert.Null(missing);
        Assert.NotNull(present);
        Assert.Equal(4, ReadProperty<int>(present, "Offset"));
        Assert.Equal(7, ReadProperty<int>(present, "Length"));
        Assert.Equal(
            ["Offset", "Length"],
            present.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public void RoleAndConfidenceMapping_PreserveAvailableValues()
    {
        Assert.Equal(
            DocumentTextKind.Header,
            ContentUnderstandingClient.ToTextKind(SemanticRole.Title));
        Assert.Equal(
            DocumentTextKind.Paragraph,
            ContentUnderstandingClient.ToTextKind(SemanticRole.Footnote));
        Assert.Equal(
            DocumentTextKind.Paragraph,
            ContentUnderstandingClient.ToTextKind(null));

        DocumentConfidence zero = Assert.IsType<DocumentConfidence>(
            ProviderAdapterHelpers.CreateConfidence(0));
        Assert.Equal(0, zero.Value);
        Assert.Equal("provider", zero.Kind);
        Assert.Null(ProviderAdapterHelpers.CreateConfidence(null));
    }

    private static T ReadProperty<T>(object value, string name)
        => (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private static DocumentExtractionTableCell Cell(int row, int column, string markdown)
    {
        var content = new DocumentExtractionText(
            $"cell:{row}:{column}",
            row * 2 + column,
            DocumentTextKind.Paragraph,
            markdown,
            markdown);
        return new DocumentExtractionTableCell(row, column, content);
    }
}
