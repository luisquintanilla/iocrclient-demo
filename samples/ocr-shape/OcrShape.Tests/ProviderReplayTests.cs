using System.Text;

using Azure.Core;
using Azure.AI.ContentUnderstanding;
using Azure.AI.DocumentIntelligence;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr.Tests;

public sealed class ProviderReplayTests
{
    private static readonly byte[] s_documentIntelligenceResult = Encoding.UTF8.GetBytes(
        """
        {
          "status": "succeeded",
          "createdDateTime": "2026-01-01T00:00:00Z",
          "lastUpdatedDateTime": "2026-01-01T00:00:01Z",
          "analyzeResult": {
            "apiVersion": "2024-11-30",
            "modelId": "prebuilt-layout",
            "stringIndexType": "utf16CodeUnit",
            "content": "# Replay title",
            "contentFormat": "markdown",
            "pages": [
              {
                "pageNumber": 1,
                "angle": 0,
                "width": 300,
                "height": 400,
                "unit": "pixel",
                "words": [],
                "selectionMarks": [],
                "lines": [],
                "barcodes": [],
                "formulas": [],
                "spans": [{"offset": 0, "length": 14}]
              },
              {
                "pageNumber": 2,
                "angle": 0,
                "width": 300,
                "height": 400,
                "unit": "pixel",
                "words": [],
                "selectionMarks": [],
                "lines": [],
                "barcodes": [],
                "formulas": [],
                "spans": []
              }
            ],
            "paragraphs": [
              {
                "spans": [{"offset": 0, "length": 14}],
                "boundingRegions": [
                  {"pageNumber": 1, "polygon": [10,10,200,10,200,30,10,30]}
                ],
                "role": "title",
                "content": "# Replay title"
              }
            ],
            "tables": [],
            "styles": []
          }
        }
        """);

    private static readonly byte[] s_contentUnderstandingResult = Encoding.UTF8.GetBytes(
        """
        {
          "status": "succeeded",
          "id": "cu-replay",
          "result": {
            "analyzerId": "prebuilt-document",
            "apiVersion": "2025-11-01",
            "createdAt": "2026-01-01T00:00:00Z",
            "warnings": [],
            "contents": [
              {
                "kind": "document",
                "mimeType": "application/pdf",
                "markdown": "# Replay title\n\n<!-- PageBreak -->\n\n",
                "unit": "pixel",
                "pages": [
                  {
                    "pageNumber": 1,
                    "angle": 0,
                    "width": 300,
                    "height": 400,
                    "spans": [],
                    "words": [],
                    "lines": [],
                    "barcodes": [],
                    "formulas": []
                  },
                  {
                    "pageNumber": 2,
                    "angle": 0,
                    "width": 300,
                    "height": 400,
                    "spans": [],
                    "words": [],
                    "lines": [],
                    "barcodes": [],
                    "formulas": []
                  }
                ],
                "paragraphs": [
                  {
                    "content": "# Replay title",
                    "role": "title",
                    "span": {"offset": 0, "length": 14},
                    "source": "D(1,10,10,200,10,200,30,10,30)"
                  }
                ],
                "tables": [],
                "figures": []
              }
            ]
          }
        }
        """);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DocumentIntelligence_CompletedLroMapsBlankPagesAndHonorsRawCapture(
        bool captureRawOutput)
    {
        const string operationLocation = "https://replay.test/di/operations/1";
        var transport = new AzureSdkReplayTransport(
            ReplayResponse.Accepted(operationLocation),
            ReplayResponse.Json(s_documentIntelligenceResult));
        var clientOptions = new DocumentIntelligenceClientOptions { Transport = transport };
        using var client = new AzureDocumentIntelligenceClient(
            new Uri("https://replay.test"),
            new FixedCredential("fake"),
            clientOptions: clientOptions);
        var options = new DocumentExtractionOptions { CaptureRawOutput = captureRawOutput };

        DocumentExtractionPage[] pages = await CollectAsync(
            client,
            options,
            "di-replay.pdf");

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(RequestMethod.Post, transport.Requests[0].Method);
        Assert.StartsWith(operationLocation, transport.Requests[1].Uri.AbsoluteUri);
        Assert.Equal([1, 2], pages.Select(page => page.PageNumber));
        Assert.Equal("# Replay title", pages[0].Markdown);
        Assert.Empty(pages[1].Observations);
        Assert.Equal(string.Empty, pages[1].Markdown);
        Assert.Equal(
            "di-replay.pdf:azure-document-intelligence:page:2",
            Assert.IsType<DocumentSourcePage>(pages[1].SourcePage).Identifier);
        AssertRawCapture(
            Assert.Single(pages[0].SourceOutputs),
            captureRawOutput,
            s_documentIntelligenceResult);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContentUnderstanding_CompletedLroMapsBlankPagesAndHonorsRawCapture(
        bool captureRawOutput)
    {
        const string operationLocation = "https://replay.test/cu/operations/1";
        var transport = new AzureSdkReplayTransport(
            ReplayResponse.Accepted(operationLocation),
            ReplayResponse.Json(s_contentUnderstandingResult));
        var clientOptions = new ContentUnderstandingClientOptions { Transport = transport };
        using var client = new ContentUnderstandingClient(
            new Uri("https://replay.test"),
            new FixedCredential("fake"),
            clientOptions: clientOptions);
        var options = new DocumentExtractionOptions { CaptureRawOutput = captureRawOutput };

        DocumentExtractionPage[] pages = await CollectAsync(
            client,
            options,
            "cu-replay.pdf");

        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(RequestMethod.Post, transport.Requests[0].Method);
        Assert.StartsWith(operationLocation, transport.Requests[1].Uri.AbsoluteUri);
        Assert.Equal([1, 2], pages.Select(page => page.PageNumber));
        Assert.Equal("# Replay title", pages[0].Markdown);
        Assert.Empty(pages[1].Observations);
        Assert.Equal(string.Empty, pages[1].Markdown);
        Assert.Equal(
            "cu-replay.pdf:azure-content-understanding:page:2",
            Assert.IsType<DocumentSourcePage>(pages[1].SourcePage).Identifier);
        AssertRawCapture(
            Assert.Single(pages[0].SourceOutputs),
            captureRawOutput,
            s_contentUnderstandingResult);
    }

    private static async Task<DocumentExtractionPage[]> CollectAsync(
        IDocumentExtractionClient client,
        DocumentExtractionOptions options,
        string identifier)
    {
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            new MemoryStream([1, 2, 3], writable: false),
            identifier,
            "application/pdf",
            options))
        {
            pages.Add(page);
        }

        return pages.ToArray();
    }

    private static void AssertRawCapture(
        DocumentSourceOutput output,
        bool captureRawOutput,
        byte[] expected)
    {
        if (!captureRawOutput)
        {
            Assert.Equal(DocumentRawContentStatus.Disabled, output.NativeStatus);
            Assert.Equal(DocumentRawContentStatus.Disabled, output.ContentStatus);
            Assert.Null(output.GetRawRepresentation());
            Assert.Null(output.GetRawContent());
            return;
        }

        Assert.Equal(DocumentRawContentStatus.Retained, output.NativeStatus);
        Assert.Equal(DocumentRawContentStatus.Retained, output.ContentStatus);
        Assert.NotNull(output.GetRawRepresentation());
        DocumentArtifact artifact = Assert.IsType<DocumentArtifact>(output.GetRawContent());
        using var copy = new MemoryStream();
        artifact.Content.CopyTo(copy);
        Assert.Equal(expected, copy.ToArray());
    }
}
