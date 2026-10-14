using System.Net;
using System.Text.Json;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr.Tests;

public sealed class MistralTableFidelityTests
{
    [Theory]
    [InlineData("html", true)]
    [InlineData("html", false)]
    [InlineData("markdown", true)]
    [InlineData("markdown", false)]
    public async Task ExtractPages_CurrentTableContentPreservesFormatPlacementAndRawPolicy(
        string format,
        bool captureRawOutput)
    {
        string content = format == "html"
            ? "<table><tr><th colspan=\"2\">0RB1T</th></tr><tr><td></td><td>17.50</td></tr></table>"
            : "| 0RB1T | Amount |\n| --- | --- |\n|  | 17.50 |";
        const string pageMarkdown = "before\n[tbl-0](tbl-0)\nbetween\n[tbl-1](tbl-1)\nafter";
        byte[] response = Response(pageMarkdown,
            new { id = "tbl-1", content, format },
            new { id = "tbl-0", content, format });
        using var handler = new ResponseHandler(response);
        using var http = new HttpClient(handler);
        using var client = new FoundryMistralOcrClient(
            new Uri("https://unit.invalid"),
            new FixedCredential("fake"),
            httpClient: http);
        using var input = new MemoryStream([1, 2, 3], writable: false);
        DocumentExtractionPage page = await ExtractPage(
            client, input, captureRawOutput);

        Assert.Equal(1, handler.Calls);
        Assert.True(input.CanRead);
        Assert.Equal(pageMarkdown, page.Markdown);
        Assert.Equal(
            ["before\n", content, "\nbetween\n", content, "\nafter"],
            page.Observations.Select(observation => observation.Markdown));
        DocumentExtractionTable[] tables = page.Observations
            .OfType<DocumentExtractionTable>().ToArray();
        Assert.Equal(2, tables.Length);
        Assert.NotEqual(tables[0].ProviderId, tables[1].ProviderId);
        DocumentSourceOutput output = Assert.Single(page.SourceOutputs);
        Assert.All(tables, table =>
        {
            Assert.Equal((0, 0), (table.RowCount, table.ColumnCount));
            Assert.Empty(table.Cells);
            Assert.Equal(page.SourcePage!.Identifier, Assert.Single(table.PageReferences).Value);
            DocumentAnnotation annotation = Assert.Single(table.Annotations);
            Assert.Same(output, annotation.SourceOutput);
            var metadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(annotation.Value);
            Assert.Equal(format, metadata["format"]);
            Assert.Equal("provider-link", metadata["placement"]);
            if (!captureRawOutput)
            {
                Assert.Null(annotation.GetRawRepresentation());
            }
        });
        Assert.Equal(
            ["tbl-0", "tbl-1"],
            tables.Select(table =>
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                    Assert.Single(table.Annotations).Value)["id"]));
        Assert.All(page.Observations.SelectMany(observation => observation.Annotations), annotation =>
        {
            Assert.Equal(captureRawOutput, annotation.GetRawRepresentation() is not null);
        });
        if (captureRawOutput)
        {
            JsonElement native = Assert.IsType<JsonElement>(output.GetRawRepresentation());
            Assert.Equal(content, native.GetProperty("pages")[0].GetProperty("tables")[0]
                .GetProperty("content").GetString());
            DocumentArtifact raw = Assert.IsType<DocumentArtifact>(output.GetRawContent());
            using var copy = new MemoryStream();
            await raw.Content.CopyToAsync(copy);
            Assert.Equal(response, copy.ToArray());
        }
        else
        {
            Assert.Equal(DocumentRawContentStatus.Disabled, output.NativeStatus);
            Assert.Equal(DocumentRawContentStatus.Disabled, output.ContentStatus);
            Assert.Null(output.GetRawRepresentation());
            Assert.Null(output.GetRawContent());
        }
    }

    [Theory]
    [InlineData("no marker")]
    [InlineData("[tbl-0](tbl-0) repeated [tbl-0](tbl-0)")]
    public async Task ExtractPages_UnplaceableTablePreservesContentAndReportsUnknownPlacement(
        string markdown)
    {
        const string content = "| supplied |";
        using var handler = new ResponseHandler(
            Response(markdown, new { id = "tbl-0", content, format = "markdown" }));
        using var http = new HttpClient(handler);
        using var client = new FoundryMistralOcrClient(
            new Uri("https://unit.invalid"), new FixedCredential("fake"), httpClient: http);
        using var input = new MemoryStream([1], writable: false);

        DocumentExtractionPage page = await ExtractPage(client, input, false);

        Assert.Equal(markdown, page.Markdown);
        Assert.Equal(
            [markdown, content],
            page.Observations.Select(observation => observation.Markdown));
        DocumentExtractionTable table = Assert.Single(page.Observations.OfType<DocumentExtractionTable>());
        var metadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(table.Annotations).Value);
        Assert.Equal("unplaced", metadata["placement"]);
        Assert.Empty(table.Cells);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("| 0RB1T |")]
    public async Task ExtractPages_CurrentContentWinsOverLegacyMarkdownWithoutReplacingEmptyOutput(
        string content)
    {
        using var handler = new ResponseHandler(Response(
            "[tbl-0](tbl-0)",
            new { id = "tbl-0", content, markdown = "| legacy |" }));
        using var http = new HttpClient(handler);
        using var client = new FoundryMistralOcrClient(
            new Uri("https://unit.invalid"), new FixedCredential("fake"), httpClient: http);
        using var input = new MemoryStream([1], writable: false);

        DocumentExtractionPage page = await ExtractPage(client, input, false);

        DocumentExtractionTable table = Assert.IsType<DocumentExtractionTable>(Assert.Single(page.Observations));
        Assert.Equal(content, table.Markdown);
        var metadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            Assert.Single(table.Annotations).Value);
        Assert.Null(metadata["format"]);
        Assert.Equal("provider-link", metadata["placement"]);
        Assert.Empty(table.Cells);
    }

    [Fact]
    public async Task ExtractPages_OverlappingContentDoesNotClaimASecondPlacement()
    {
        using var handler = new ResponseHandler(Response(
            "before abcdef after",
            new { markdown = "abc" },
            new { markdown = "abcdef" }));
        using var http = new HttpClient(handler);
        using var client = new FoundryMistralOcrClient(
            new Uri("https://unit.invalid"), new FixedCredential("fake"), httpClient: http);
        using var input = new MemoryStream([1], writable: false);

        DocumentExtractionPage page = await ExtractPage(client, input, false);

        Assert.Equal("before abcdef after", page.Markdown);
        Assert.Equal(
            ["before ", "abc", "def after", "abcdef"],
            page.Observations.Select(observation => observation.Markdown));
        DocumentExtractionTable[] tables = page.Observations.OfType<DocumentExtractionTable>().ToArray();
        Assert.Equal(
            ["inline-content", "unplaced"],
            tables.Select(table => Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                Assert.Single(table.Annotations).Value)["placement"]));
    }

    private static async Task<DocumentExtractionPage> ExtractPage(
        IDocumentExtractionClient client, Stream input, bool captureRawOutput)
    {
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            input, "mistral-table.pdf", "application/pdf",
            new DocumentExtractionOptions { CaptureRawOutput = captureRawOutput }))
        {
            pages.Add(page);
        }

        return Assert.Single(pages);
    }

    private static byte[] Response(string markdown, params object[] tables)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            pages = new[]
            {
                new
                {
                    index = 0,
                    markdown,
                    dimensions = new { width = 640, height = 480 },
                    images = Array.Empty<object>(),
                    tables,
                },
            },
        });

    private sealed class ResponseHandler(byte[] response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response),
            });
        }
    }
}
