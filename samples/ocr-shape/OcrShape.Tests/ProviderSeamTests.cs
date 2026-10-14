using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Azure.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.Logging;

namespace DemoOcr.Tests;

public sealed class FoundryMistralOcrClientTests
{
    private static readonly byte[] s_successBytes = Encoding.UTF8.GetBytes(
        """
        {"pages":[{"index":0,"markdown":"before\n| A | B |\nafter","dimensions":{"width":640,"height":480},"tables":[{"markdown":"| A | B |"}]}]}
        """);

    [Fact]
    public async Task ExtractPages_EachRetryRequestCarriesLocalBearerAuthAndFifthResponseSucceeds()
    {
        using var handler = new QueueHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        using var client = new FoundryMistralOcrClient(
            new Uri("https://unit.invalid/base"),
            new FixedCredential("request-token"),
            "fixed-model",
            includeImages: false,
            http,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        DocumentExtractionPage page = Assert.Single(await CollectAsync(client));

        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(new Uri("https://unit.invalid/base/providers/mistral/azure/ocr"), request.Uri);
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal("request-token", request.Authorization?.Parameter);
            Assert.Contains("\"model\":\"fixed-model\"", request.Body, StringComparison.Ordinal);
        });
        Assert.Equal(
            [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(12)],
            delays);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal("before\n| A | B |\nafter", page.Markdown);
    }

    [Fact]
    public async Task ExtractPages_RetainsExactResponseBytesAndDetachedJsonElement()
    {
        using var handler = new QueueHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentExtractionPage page = Assert.Single(await CollectAsync(client, options));
        DocumentSourceOutput output = Assert.Single(page.SourceOutputs);
        JsonElement native = Assert.IsType<JsonElement>(output.GetRawRepresentation());
        DocumentArtifact artifact = Assert.IsType<DocumentArtifact>(output.GetRawContent());
        using var copy = new MemoryStream();
        artifact.Content.CopyTo(copy);

        Assert.Equal(DocumentRawContentStatus.Retained, output.NativeStatus);
        Assert.Equal(DocumentRawContentStatus.Retained, output.ContentStatus);
        Assert.Equal(s_successBytes, copy.ToArray());
        Assert.Equal(JsonValueKind.Object, native.ValueKind);
        Assert.Equal(0, native.GetProperty("pages")[0].GetProperty("index").GetInt32());
        Assert.Equal("application/json", artifact.MediaType);
    }

    [Fact]
    public async Task ExtractPages_TableMarkdownCreatesTableWithoutFabricatedCells()
    {
        using var handler = new QueueHandler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        DocumentExtractionPage page = Assert.Single(await CollectAsync(client));
        DocumentExtractionTable table = Assert.Single(
            page.Observations.OfType<DocumentExtractionTable>());

        Assert.Equal("| A | B |", table.Markdown);
        Assert.Equal(0, table.RowCount);
        Assert.Equal(0, table.ColumnCount);
        Assert.Empty(table.Cells);
        Assert.Equal(page.SourcePage!.Identifier, Assert.Single(table.PageReferences).Value);
        Assert.Equal(
            ["before\n", "| A | B |", "\nafter"],
            page.Observations.Select(observation => observation.Markdown));
    }

    [Fact]
    public async Task ExtractPages_NonRetryableFailureThrowsImmediately()
    {
        using var handler = new QueueHandler(HttpStatusCode.BadRequest);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await CollectAsync(client));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Contains("400 (Bad Request)", error.Message);
    }

    [Fact]
    public async Task ExtractPages_FifthServiceUnavailableEndsRetrySequence()
    {
        using var handler = new QueueHandler(
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        using var client = new FoundryMistralOcrClient(
            new Uri("https://unit.invalid"),
            new FixedCredential("token"),
            "fixed-model",
            false,
            http,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await CollectAsync(client));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(4, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(12), delays[^1]);
    }

    private static FoundryMistralOcrClient CreateClient(HttpClient http)
        => new(
            new Uri("https://unit.invalid"),
            new FixedCredential("token"),
            "fixed-model",
            false,
            http,
            (_, _) => Task.CompletedTask);

    private static async Task<DocumentExtractionPage[]> CollectAsync(
        FoundryMistralOcrClient client,
        DocumentExtractionOptions? options = null)
    {
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            new MemoryStream([10, 20, 30], writable: false),
            "mistral.pdf",
            "application/pdf",
            options))
        {
            pages.Add(page);
        }

        return pages.ToArray();
    }

    private sealed record RequestCapture(
        Uri? Uri,
        AuthenticationHeaderValue? Authorization,
        string Body);

    private sealed class QueueHandler(params HttpStatusCode[] statusCodes) : HttpMessageHandler
    {
        private int _index;

        public List<RequestCapture> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestCapture(
                request.RequestUri,
                request.Headers.Authorization,
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            HttpStatusCode statusCode = statusCodes[_index++];
            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(
                    statusCode == HttpStatusCode.OK
                        ? s_successBytes
                        : Encoding.UTF8.GetBytes("""{"error":"retry"}""")),
            };
        }
    }
}

public sealed class VisionLlmOcrClientTests
{
    [Theory]
    [InlineData("schema is unsupported")]
    [InlineData("response format is unsupported")]
    [InlineData("structured output is unsupported")]
    public void CanFallbackToFreeform_ClassifiesOnlyLocalCapabilityFailures(string message)
    {
        Assert.True(VisionLlmOcrClient.CanFallbackToFreeform(
            new InvalidOperationException(message)));
        Assert.True(VisionLlmOcrClient.CanFallbackToFreeform(
            new NotSupportedException(message)));
        Assert.False(VisionLlmOcrClient.CanFallbackToFreeform(
            new InvalidOperationException("unrelated failure")));
        Assert.False(VisionLlmOcrClient.CanFallbackToFreeform(
            new HttpRequestException(message)));
        Assert.False(VisionLlmOcrClient.CanFallbackToFreeform(
            new OperationCanceledException(message)));
    }

    [Fact]
    public async Task ExtractPages_StructuredFailureThrowsWithoutFreeformFallback()
    {
        using var chat = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"notPages":true}""")));
        using var client = new VisionLlmOcrClient(chat, useStructuredOutput: true);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await CollectAsync(client, "application/pdf"));

        Assert.Equal(1, chat.Calls);
        Assert.Contains("required structured page array", error.Message);
    }

    [Fact]
    public async Task ExtractPages_SchemaCapabilityFailureFallsBackOnceAndRecordsProvenance()
    {
        object raw = new();
        using var chat = new FakeChatClient(
            new InvalidOperationException("response format is unsupported"),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "# Fallback markdown"))
            {
                ModelId = "vision-model",
                RawRepresentation = raw,
            });
        var logger = new RecordingLogger();
        using var client = new VisionLlmOcrClient(
            chat,
            useStructuredOutput: true,
            logger: logger);
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentExtractionPage page = Assert.Single(
            await CollectAsync(client, "image/png", options));
        DocumentAnnotation fallback = Assert.Single(page.Annotations);
        DocumentSourceOutput output = Assert.Single(page.SourceOutputs);
        var value = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fallback.Value);

        Assert.Equal(2, chat.Calls);
        Assert.Equal("# Fallback markdown", page.Markdown);
        Assert.Equal("provider-capability", value["reason"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, value["exceptionType"]);
        Assert.Equal(true, value["structuredOutputRequested"]);
        Assert.Same(output, fallback.SourceOutput);
        Assert.Same(raw, fallback.GetRawRepresentation());
        Assert.Same(raw, output.GetRawRepresentation());
        Assert.Equal(
            VisionLlmOcrClient.StructuredOutputFallbackEventId,
            Assert.Single(logger.Events));
    }

    [Fact]
    public async Task ExtractPages_FreeformRejectsNonImageBeforeCallingChatClient()
    {
        using var chat = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        using var client = new VisionLlmOcrClient(chat);

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await CollectAsync(client, "application/octet-stream"));

        Assert.Equal(0, chat.Calls);
        Assert.Contains("supports image/* and application/pdf", error.Message);
    }

    [Fact]
    public async Task ExtractPages_PdfRendersEveryPageToPngAndPreservesBlankPageIdentity()
    {
        object firstRaw = new();
        object secondRaw = new();
        using var chat = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "# First page"))
            {
                ModelId = "vision-model",
                RawRepresentation = firstRaw,
            },
            new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
            {
                ModelId = "vision-model",
                RawRepresentation = secondRaw,
            });
        using var client = new VisionLlmOcrClient(
            chat,
            pdfRasterizer: new PdfPageRasterizer(72));
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("render this", Width: 300, Height: 400),
            new PdfTestPage(Width: 300, Height: 400));
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentExtractionPage[] pages = await CollectAsync(
            client,
            "application/pdf",
            options,
            pdf);

        Assert.Equal(2, chat.Calls);
        Assert.Equal([1, 2], pages.Select(page => page.PageNumber));
        Assert.Equal("# First page", pages[0].Markdown);
        Assert.Empty(pages[1].Observations);
        Assert.Equal(string.Empty, pages[1].Markdown);
        Assert.Equal(
            [
                "vision-input:vision-llm:page:1",
                "vision-input:vision-llm:page:2",
            ],
            pages.Select(page => Assert.IsType<DocumentSourcePage>(page.SourcePage).Identifier));
        Assert.All(
            pages,
            page => Assert.IsType<PdfRasterTransform>(
                page.Annotations.Single(annotation => annotation.Kind == "pdf-raster-transform").Value));
        Assert.Same(firstRaw, Assert.Single(pages[0].SourceOutputs).GetRawRepresentation());
        Assert.Same(secondRaw, Assert.Single(pages[1].SourceOutputs).GetRawRepresentation());
        Assert.Equal(2, chat.Requests.Count);
        Assert.All(
            chat.Requests,
            request =>
            {
                DataContent image = Assert.IsType<DataContent>(
                    request.Single().Contents.Single(content => content is DataContent));
                Assert.Equal("image/png", image.MediaType);
                ReadOnlyMemory<byte> png = Assert.IsType<ReadOnlyMemory<byte>>(image.Data);
                Assert.True(png.Span.StartsWith(new byte[] { 137, 80, 78, 71 }));
            });
    }

    [Fact]
    public async Task ExtractPages_StructuredSchemaFailureOnPdfFallsBackPerRenderedPage()
    {
        using var chat = new FakeChatClient(
            new NotSupportedException("JSON schema is unsupported"),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "page one")),
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "page two")));
        var logger = new RecordingLogger();
        using var client = new VisionLlmOcrClient(
            chat,
            useStructuredOutput: true,
            logger: logger,
            pdfRasterizer: new PdfPageRasterizer(72));
        byte[] pdf = TestPdfFactory.Create(
            new PdfTestPage("one"),
            new PdfTestPage("two"));

        DocumentExtractionPage[] pages = await CollectAsync(
            client,
            "application/pdf",
            content: pdf);

        Assert.Equal(3, chat.Calls);
        Assert.Equal(["page one", "page two"], pages.Select(page => page.Markdown));
        Assert.All(
            pages,
            page =>
            {
                DocumentAnnotation fallback = page.Annotations.Single(
                    annotation => annotation.Kind == "structured-output-fallback");
                Assert.Same(Assert.Single(page.SourceOutputs), fallback.SourceOutput);
            });
        Assert.Equal(
            VisionLlmOcrClient.StructuredOutputFallbackEventId,
            Assert.Single(logger.Events));
    }

    [Fact]
    public async Task ExtractPages_PdfCancellationStopsBeforeChatCalls()
    {
        using var chat = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "unused")));
        using var client = new VisionLlmOcrClient(
            chat,
            pdfRasterizer: new PdfPageRasterizer(72));
        byte[] pdf = TestPdfFactory.Create(new PdfTestPage("cancel"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CollectAsync(
                client,
                "application/pdf",
                content: pdf,
                cancellationToken: cancellation.Token));

        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task ExtractPages_FreeformAnnotationUsesOnlyResponseRawRepresentation()
    {
        object raw = new();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "# Exact markdown"))
        {
            ModelId = "vision-model",
            RawRepresentation = raw,
        };
        using var chat = new FakeChatClient(response);
        using var client = new VisionLlmOcrClient(chat);
        var options = new DocumentExtractionOptions { CaptureRawOutput = true };

        DocumentExtractionPage page = Assert.Single(
            await CollectAsync(client, "image/png", options));
        DocumentExtractionText text = Assert.IsType<DocumentExtractionText>(
            Assert.Single(page.Observations));
        DocumentAnnotation annotation = Assert.Single(text.Annotations);
        DocumentSourceOutput output = Assert.Single(page.SourceOutputs);

        Assert.Equal("# Exact markdown", page.Markdown);
        Assert.Same(raw, annotation.GetRawRepresentation());
        Assert.Same(raw, output.GetRawRepresentation());
        Assert.NotSame(response, output.GetRawRepresentation());
        Assert.Equal(DocumentRawContentStatus.Unavailable, output.ContentStatus);
    }

    [Fact]
    public async Task ExtractPages_StructuredTablePreservesMarkdownWithoutFabricatedCells()
    {
        const string json =
            """{"pages":[{"index":0,"markdown":"page","tables":[{"rowCount":2,"columnCount":2,"markdown":"|a|b|"}],"figures":[],"language":"en","confidence":0.9}]}""";
        object raw = new();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, json))
        {
            RawRepresentation = raw,
        };
        using var chat = new FakeChatClient(response);
        using var client = new VisionLlmOcrClient(chat, useStructuredOutput: true);

        DocumentExtractionPage page = Assert.Single(
            await CollectAsync(client, "application/pdf"));
        DocumentExtractionTable table = Assert.Single(
            page.Observations.OfType<DocumentExtractionTable>());

        Assert.Equal((2, 2), (table.RowCount, table.ColumnCount));
        Assert.Equal("|a|b|", table.Markdown);
        Assert.Empty(table.Cells);
        Assert.Equal(
            "unavailable-from-provider",
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                Assert.Single(table.Annotations).Value)["cellStatus"]);
        Assert.Same(raw, Assert.Single(table.Annotations).GetRawRepresentation());
    }

    [Fact]
    public async Task ExtractPages_StructuredOutOfRangeConfidenceIsRejected()
    {
        const string json =
            """{"pages":[{"index":0,"markdown":"page","tables":[],"figures":[],"language":"en","confidence":1.01}]}""";
        using var chat = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        using var client = new VisionLlmOcrClient(chat, useStructuredOutput: true);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await CollectAsync(client, "application/pdf"));

        Assert.Equal(1, chat.Calls);
        Assert.Contains("confidence '1.01' is outside [0,1]", error.Message);
    }

    [Fact]
    public async Task ExtractPages_StructuredOutputPreservesLiteralContentAndConfidenceWithoutRaw()
    {
        const string json =
            """{"pages":[{"index":2,"markdown":"0RB1T\n| A |  |\n|---|---|","tables":[{"rowCount":2,"columnCount":2,"markdown":"| A |  |\n|---|---|"}],"figures":[],"language":"und","confidence":0}]}""";
        using var chat = new FakeChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, json)) { RawRepresentation = new object() });
        using var client = new VisionLlmOcrClient(chat, useStructuredOutput: true);

        DocumentExtractionPage page = Assert.Single(await CollectAsync(
            client, "image/png", new DocumentExtractionOptions { CaptureRawOutput = false }));

        Assert.Equal(3, page.PageNumber);
        Assert.Equal("0RB1T\n| A |  |\n|---|---|", page.Markdown);
        DocumentExtractionText text = Assert.Single(page.Observations.OfType<DocumentExtractionText>());
        Assert.Equal(page.Markdown, text.Text);
        DocumentExtractionTable table = Assert.Single(page.Observations.OfType<DocumentExtractionTable>());
        Assert.Equal((2, 2), (table.RowCount, table.ColumnCount));
        Assert.Equal("| A |  |\n|---|---|", table.Markdown);
        Assert.Empty(table.Cells);
        DocumentSourcePage frame = Assert.IsType<DocumentSourcePage>(page.SourcePage);
        Assert.Null(frame.Dimensions);
        Assert.Null(frame.CoordinateUnit);
        Assert.Null(frame.CoordinateOrigin);
        Assert.Equal(frame.Identifier, Assert.Single(table.PageReferences).Value);
        DocumentAnnotation annotation = Assert.Single(page.Annotations);
        Assert.Equal("und", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(annotation.Value)["language"]);
        DocumentConfidence confidence = Assert.IsType<DocumentConfidence>(annotation.Confidence);
        Assert.Equal(0, confidence.Value);
        Assert.Equal("model-self-reported", confidence.Kind);
        Assert.Empty(annotation.Regions);
        Assert.Null(annotation.GetRawRepresentation());
        DocumentSourceOutput output = Assert.Single(page.SourceOutputs);
        Assert.Equal(DocumentRawContentStatus.Disabled, output.NativeStatus);
        Assert.Null(output.GetRawRepresentation());
        Assert.Null(output.GetRawContent());
        Assert.Equal(1, chat.Calls);
    }

    private static async Task<DocumentExtractionPage[]> CollectAsync(
        VisionLlmOcrClient client,
        string mediaType,
        DocumentExtractionOptions? options = null,
        byte[]? content = null,
        CancellationToken cancellationToken = default)
    {
        var pages = new List<DocumentExtractionPage>();
        await foreach (DocumentExtractionPage page in client.ExtractPagesAsync(
            new MemoryStream(content ?? [1, 2, 3], writable: false),
            "vision-input",
            mediaType,
            options,
            cancellationToken))
        {
            pages.Add(page);
        }

        return pages.ToArray();
    }

    private sealed class FakeChatClient(params object[] results) : IChatClient
    {
        private readonly Queue<object> _results = new(results);

        public int Calls { get; private set; }
        public List<ChatMessage[]> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Requests.Add(messages.ToArray());
            object result = _results.Dequeue();
            return result is Exception exception
                ? Task.FromException<ChatResponse>(exception)
                : Task.FromResult((ChatResponse)result);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger<VisionLlmOcrClient>
    {
        public List<EventId> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Events.Add(eventId);
    }
}

internal sealed class FixedCredential(string token) : TokenCredential
{
    public override AccessToken GetToken(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
        => new(token, DateTimeOffset.MaxValue);

    public override ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new AccessToken(token, DateTimeOffset.MaxValue));
}
