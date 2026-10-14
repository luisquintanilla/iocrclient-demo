using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.AI.ContentUnderstanding;
using Azure.AI.DocumentIntelligence;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using DocumentAnnotation = Microsoft.Extensions.Documents.DocumentAnnotation;
using DocumentTableCell = Microsoft.Extensions.Documents.DocumentTableCell;

namespace DemoOcr.Tests;

public sealed class ProviderFidelityTests
{
    private static readonly float[] s_polygon = [1, 2, 3, 2, 3, 4, 2, 5, 1, 4];

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AzureTables_PreserveCellsSpansRolesAndPageEvidence(
        bool contentUnderstanding,
        bool captureRawOutput)
    {
        byte[] response = CreateResponse(contentUnderstanding);
        var transport = new AzureSdkReplayTransport(
            ReplayResponse.Accepted("https://replay.test/operations/table"),
            ReplayResponse.Json(response));
        using IDocumentExtractionClient client = contentUnderstanding
            ? new ContentUnderstandingClient(
                new Uri("https://replay.test"),
                new FixedCredential("fake"),
                clientOptions: new ContentUnderstandingClientOptions { Transport = transport })
            : new AzureDocumentIntelligenceClient(
                new Uri("https://replay.test"),
                new FixedCredential("fake"),
                clientOptions: new DocumentIntelligenceClientOptions { Transport = transport });
        using var input = new MemoryStream([1, 2, 3], writable: false);
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            input,
            "table.pdf",
            "application/pdf",
            new DocumentExtractionOptions { CaptureRawOutput = captureRawOutput }))
        {
            pages.Add(page);
        }

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal([1, 2], pages.Select(page => page.PageNumber));
        DocumentSourceOutput output = Assert.Single(pages[0].SourceOutputs);
        DocumentExtractionTable firstTable = Assert.Single(
            pages[0].Observations.OfType<DocumentExtractionTable>());
        Assert.Empty(pages[1].Observations.OfType<DocumentExtractionTable>());
        Assert.Equal((3, 3), (firstTable.RowCount, firstTable.ColumnCount));
        Assert.Equal(
            [(0, 0), (1, 0), (1, 1), (1, 2), (2, 2)],
            firstTable.Cells.Select(cell => (cell.Row, cell.Column)));
        Assert.Equal(
            ["Supplied header", "0RB1T|<&", "", "USD", "17.50"],
            firstTable.Cells.Select(cell => Assert.IsType<DocumentExtractionText>(cell.Content).Text));
        Assert.Equal(
            [(1, 3), (2, 1), (1, 1), (1, 1), (1, 1)],
            firstTable.Cells.Select(cell => (cell.RowSpan, cell.ColumnSpan)));
        Assert.Equal(
            ["columnHeader", "rowHeader", "content", "content", "producerSpecificRole"],
            firstTable.Cells.Select(cell => cell.Kind));
        Assert.DoesNotContain(firstTable.Cells, cell => (cell.Row, cell.Column) == (2, 1));
        Assert.Equal(
            pages.Select(page => page.SourcePage!.Identifier),
            firstTable.PageReferences.Select(reference => reference.Value));

        DocumentAnnotation tableAnnotation = Assert.Single(firstTable.Annotations);
        Assert.Equal(2, tableAnnotation.Regions.Count);
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            DocumentSourcePage frame = Assert.IsType<DocumentSourcePage>(pages[pageIndex].SourcePage);
            Assert.Equal(DocumentCoordinateUnit.Inch, frame.CoordinateUnit);
            Assert.Equal(DocumentCoordinateOrigin.TopLeft, frame.CoordinateOrigin);
            DocumentPageDimensions dimensions = Assert.IsType<DocumentPageDimensions>(frame.Dimensions);
            Assert.Equal(8.5, dimensions.Width);
            Assert.Equal(11, dimensions.Height);
            Assert.Same(output, frame.SourceOutput);
            AssertPolygon(tableAnnotation.Regions[pageIndex], frame);
        }

        foreach (DocumentExtractionTableCell cell in firstTable.Cells)
        {
            DocumentAnnotation annotation = Assert.Single(cell.Annotations);
            DocumentExtractionText text = Assert.IsType<DocumentExtractionText>(cell.Content);
            int pageIndex = cell.Row == 2 ? 1 : 0;
            DocumentSourcePage frame = pages[pageIndex].SourcePage!;
            AssertPolygon(Assert.Single(annotation.Regions), frame);
            Assert.Equal(frame.Identifier, Assert.Single(text.PageReferences).Value);
            Assert.Same(output, annotation.SourceOutput);
            Assert.Equal(cell.Kind, annotation.Role);
            Assert.Null(annotation.Confidence);
            Assert.Same(annotation, Assert.Single(text.Annotations));
            if (captureRawOutput)
            {
                if (contentUnderstanding)
                {
                    var native = Assert.IsType<Azure.AI.ContentUnderstanding.DocumentTableCell>(
                        annotation.GetRawRepresentation());
                    Assert.Equal(text.Text, native.Content);
                    Assert.Equal((cell.Row, cell.Column), (native.RowIndex, native.ColumnIndex));
                }
                else
                {
                    var native = Assert.IsType<Azure.AI.DocumentIntelligence.DocumentTableCell>(
                        annotation.GetRawRepresentation());
                    Assert.Equal(text.Text, native.Content);
                    Assert.Equal((cell.Row, cell.Column), (native.RowIndex, native.ColumnIndex));
                }
            }
            else
            {
                Assert.Null(annotation.GetRawRepresentation());
            }
        }

        Assert.Equal(
            captureRawOutput ? DocumentRawContentStatus.Retained : DocumentRawContentStatus.Disabled,
            output.NativeStatus);
        foreach (DocumentExtractionPage page in pages)
        {
            IEnumerable<DocumentAnnotation> annotations = page.Annotations
                .Concat(page.Observations.SelectMany(observation => observation.Annotations));
            Assert.All(annotations, annotation =>
            {
                Assert.Same(output, annotation.SourceOutput);
                if (captureRawOutput)
                {
                    Assert.NotNull(annotation.GetRawRepresentation());
                }
                else
                {
                    Assert.Null(annotation.GetRawRepresentation());
                }
            });
        }

        if (captureRawOutput)
        {
            DocumentArtifact raw = Assert.IsType<DocumentArtifact>(output.GetRawContent());
            using var copy = new MemoryStream();
            await raw.Content.CopyToAsync(copy);
            Assert.Equal(response, copy.ToArray());
        }
        else
        {
            Assert.Null(output.GetRawRepresentation());
            Assert.Null(output.GetRawContent());
            Assert.Equal(DocumentRawContentStatus.Disabled, output.ContentStatus);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureTables_FinalizationPreservesCrossPageIdentityAndExplicitEmptyCell(
        bool contentUnderstanding)
    {
        var transport = new AzureSdkReplayTransport(
            ReplayResponse.Accepted("https://replay.test/operations/table"),
            ReplayResponse.Json(CreateResponse(contentUnderstanding)));
        using IDocumentExtractionClient client = contentUnderstanding
            ? new ContentUnderstandingClient(
                new Uri("https://replay.test"),
                new FixedCredential("fake"),
                clientOptions: new ContentUnderstandingClientOptions { Transport = transport })
            : new AzureDocumentIntelligenceClient(
                new Uri("https://replay.test"),
                new FixedCredential("fake"),
                clientOptions: new DocumentIntelligenceClientOptions { Transport = transport });
        using var input = new MemoryStream([1, 2, 3], writable: false);

        DocumentExtractionResult result = await DocumentExtractionClientExtensions.ExtractAsync(
            client, input, "table.pdf", "application/pdf",
            new DocumentExtractionOptions { CaptureRawOutput = false });
        using Document document = result.Document;

        DocumentTableElement table = Assert.Single(document.EnumerateContent().OfType<DocumentTableElement>());
        Assert.Equal((3, 3), (table.Cells.RowCount, table.Cells.ColumnCount));
        DocumentTableCell header = Assert.IsType<DocumentTableCell>(table.Cells[0, 0]);
        Assert.Equal("columnHeader", header.Kind);
        Assert.Equal(3, header.ColumnSpan);
        DocumentTableCell empty = Assert.IsType<DocumentTableCell>(table.Cells[1, 1]);
        Assert.Equal("", Assert.IsType<DocumentTextElement>(empty.Content).Text);
        Assert.Null(table.Cells[2, 1]);
        Assert.Null(table.Cells[0, 1]);
        Assert.Equal("table.pdf:azure-" + (contentUnderstanding
            ? "content-understanding:page:1"
            : "document-intelligence:page:1"),
            Assert.Single(Assert.Single(empty.Annotations).Regions).Page.Identifier);
        Assert.Equal(
            [1, 2],
            Assert.Single(table.Annotations).Regions.Select(region => region.Page.PageNumber));
        Assert.Null(Assert.Single(empty.Annotations).GetRawRepresentation());
        Assert.Contains("0RB1T", table.ToMarkdown(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureTables_OmittedOptionalCellPropertiesPreserveSdkDefaults(
        bool contentUnderstanding)
    {
        JsonNode response = JsonNode.Parse(CreateResponse(contentUnderstanding))!;
        JsonNode result = response[contentUnderstanding ? "result" : "analyzeResult"]!;
        JsonNode content = contentUnderstanding ? result["contents"]![0]! : result;
        JsonObject cell = content["tables"]![0]!["cells"]![2]!.AsObject();
        cell.Remove("kind");
        cell.Remove("rowSpan");
        cell.Remove("columnSpan");
        var transport = new AzureSdkReplayTransport(
            ReplayResponse.Accepted("https://replay.test/operations/table"),
            ReplayResponse.Json(JsonSerializer.SerializeToUtf8Bytes(response)));
        using IDocumentExtractionClient client = contentUnderstanding
            ? new ContentUnderstandingClient(
                new Uri("https://replay.test"), new FixedCredential("fake"),
                clientOptions: new ContentUnderstandingClientOptions { Transport = transport })
            : new AzureDocumentIntelligenceClient(
                new Uri("https://replay.test"), new FixedCredential("fake"),
                clientOptions: new DocumentIntelligenceClientOptions { Transport = transport });
        using var input = new MemoryStream([1], writable: false);
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            input, "optional.pdf", "application/pdf"))
        {
            pages.Add(page);
        }

        DocumentExtractionTable table = Assert.Single(pages[0].Observations.OfType<DocumentExtractionTable>());
        DocumentExtractionTableCell empty = Assert.Single(
            table.Cells, item => (item.Row, item.Column) == (1, 1));
        Assert.Equal((1, 1), (empty.RowSpan, empty.ColumnSpan));
        Assert.Equal("", Assert.IsType<DocumentExtractionText>(empty.Content).Text);
        Assert.Equal(5, table.Cells.Count);
        Assert.Null(empty.Kind);
        Assert.Null(Assert.Single(empty.Annotations).Role);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AzureFigures_FinalizationPreservesCrossPageIdentityAndEvidence(
        bool contentUnderstanding)
    {
        var responses = new List<ReplayResponse>
        {
            ReplayResponse.Accepted(
                "https://replay.test/documentintelligence/documentModels/prebuilt-layout/analyzeResults/" +
                "00000000-0000-0000-0000-000000000001?api-version=2024-11-30"),
            ReplayResponse.Json(CreateResponse(contentUnderstanding, includeFigure: true)),
        };
        if (!contentUnderstanding)
        {
            responses.Add(new ReplayResponse(
                200, [1, 2, 3], new Dictionary<string, string> { ["content-type"] = "image/png" }));
        }

        var transport = new AzureSdkReplayTransport(responses.ToArray());
        using IDocumentExtractionClient client = contentUnderstanding
            ? new ContentUnderstandingClient(
                new Uri("https://replay.test"), new FixedCredential("fake"),
                clientOptions: new ContentUnderstandingClientOptions { Transport = transport })
            : new AzureDocumentIntelligenceClient(
                new Uri("https://replay.test"), new FixedCredential("fake"), includeImages: true,
                clientOptions: new DocumentIntelligenceClientOptions { Transport = transport });
        using var input = new MemoryStream([1], writable: false);

        DocumentExtractionResult result = await DocumentExtractionClientExtensions.ExtractAsync(
            client, input, "figure.pdf", "application/pdf",
            new DocumentExtractionOptions { CaptureRawOutput = false });
        using Document document = result.Document;

        DocumentImageElement image = Assert.Single(document.EnumerateContent().OfType<DocumentImageElement>());
        DocumentAnnotation annotation = Assert.Single(image.Annotations);
        Assert.Equal([1, 2], annotation.Regions.Select(region => region.Page.PageNumber));
        Assert.Equal(
            annotation.Regions.Select(region => region.Page.Identifier).Order(),
            image.PageReferences.Select(reference => reference.Value).Distinct().Order());
        Assert.Null(annotation.GetRawRepresentation());
        Assert.Equal(contentUnderstanding ? 2 : 3, transport.Requests.Count);
    }

    private static void AssertPolygon(DocumentRegion region, DocumentSourcePage frame)
    {
        Assert.Same(frame, region.Page);
        Assert.Equal(
            [(1d, 2d), (3d, 2d), (3d, 4d), (2d, 5d), (1d, 4d)],
            region.Polygon.Select(point => (point.X, point.Y)));
    }

    private static byte[] CreateResponse(bool contentUnderstanding, bool includeFigure = false)
    {
        object[] spans = [new { offset = 0, length = 7 }];
        object[] empty = [];
        var cells = new[]
        {
            Cell(0, 0, "Supplied header", 1, 3, "columnHeader", 1),
            Cell(1, 0, "0RB1T|<&", 2, 1, "rowHeader", 1),
            Cell(1, 1, "", 1, 1, "content", 1),
            Cell(1, 2, "USD", 1, 1, "content", 1),
            Cell(2, 2, "17.50", 1, 1, "producerSpecificRole", 2),
        };
        var table = new Dictionary<string, object?>
        {
            ["rowCount"] = 3,
            ["columnCount"] = 3,
            ["cells"] = cells,
        };
        if (contentUnderstanding)
        {
            table["source"] = $"{Source(1)};{Source(2)}";
        }
        else
        {
            table["boundingRegions"] = new[] { Region(1), Region(2) };
            table["spans"] = spans;
        }

        object[] pages = [Page(1, spans), Page(2, empty)];
        object[] figures = includeFigure
            ? [new
            {
                id = "figure-0",
                source = $"{Source(1)};{Source(2)}",
                boundingRegions = new[] { Region(1), Region(2) },
                spans = empty,
                description = "Supplied figure",
                caption = new { content = "Supplied figure" },
            }]
            : [];
        object result = contentUnderstanding
            ? new
            {
                analyzerId = "prebuilt-document",
                apiVersion = "2025-11-01",
                createdAt = "2026-01-01T00:00:00Z",
                warnings = empty,
                contents = new[]
                {
                    new
                    {
                        kind = "document",
                        mimeType = "application/pdf",
                        markdown = "[table]",
                        unit = "inch",
                        pages,
                        paragraphs = empty,
                        tables = new[] { table },
                        figures,
                    },
                },
            }
            : new
            {
                apiVersion = "2024-11-30",
                modelId = "prebuilt-layout",
                stringIndexType = "utf16CodeUnit",
                content = "[table]",
                contentFormat = "markdown",
                pages,
                paragraphs = empty,
                tables = new[] { table },
                figures,
                styles = empty,
            };
        var envelope = new Dictionary<string, object?>
        {
            ["status"] = "succeeded",
            ["createdDateTime"] = "2026-01-01T00:00:00Z",
            ["lastUpdatedDateTime"] = "2026-01-01T00:00:01Z",
            ["id"] = "table-replay",
            [contentUnderstanding ? "result" : "analyzeResult"] = result,
        };
        return JsonSerializer.SerializeToUtf8Bytes(envelope);

        object Page(int pageNumber, object[] pageSpans) => new
        {
            pageNumber,
            angle = 0,
            width = 8.5,
            height = 11,
            unit = "inch",
            spans = pageSpans,
            words = empty,
            lines = empty,
            selectionMarks = empty,
            barcodes = empty,
            formulas = empty,
        };

        Dictionary<string, object?> Cell(
            int row,
            int column,
            string text,
            int rowSpan,
            int columnSpan,
            string kind,
            int pageNumber)
        {
            var cell = new Dictionary<string, object?>
            {
                ["rowIndex"] = row,
                ["columnIndex"] = column,
                ["content"] = text,
                ["rowSpan"] = rowSpan,
                ["columnSpan"] = columnSpan,
                ["kind"] = kind,
            };
            if (contentUnderstanding)
            {
                cell["source"] = Source(pageNumber);
            }
            else
            {
                cell["boundingRegions"] = new[] { Region(pageNumber) };
                cell["spans"] = empty;
            }

            return cell;
        }

        static string Source(int pageNumber) => $"D({pageNumber},1,2,3,2,3,4,2,5,1,4)";
        static object Region(int pageNumber) => new { pageNumber, polygon = s_polygon };
    }
}
