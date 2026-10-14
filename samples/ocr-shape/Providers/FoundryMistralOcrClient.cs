using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Azure.Core;

using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by Mistral OCR on Azure AI Foundry.
/// </summary>
public sealed class FoundryMistralOcrClient : IDocumentExtractionClient
{
    private const string ProviderName = "foundry-mistral-ocr";

    private static readonly TokenRequestContext s_scope =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly Uri _endpoint;
    private readonly TokenCredential _credential;
    private readonly string _defaultModel;
    private readonly bool _includeImages;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public FoundryMistralOcrClient(
        Uri endpoint,
        TokenCredential credential,
        string defaultModel = "mistral-ocr-4-0",
        bool includeImages = false,
        HttpClient? httpClient = null)
        : this(
            endpoint,
            credential,
            defaultModel,
            includeImages,
            httpClient,
            Task.Delay)
    {
    }

    internal FoundryMistralOcrClient(
        Uri endpoint,
        TokenCredential credential,
        string defaultModel,
        bool includeImages,
        HttpClient? httpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _endpoint = endpoint;
        _credential = credential;
        _defaultModel = defaultModel;
        _includeImages = includeImages;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsHttpClient = httpClient is null;
        _delayAsync = delayAsync;
    }

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamPages(
            ct => ExtractPagesCoreAsync(document, identifier, mediaType, options, ct),
            cancellationToken);

    private async Task<IReadOnlyList<DocumentExtractionPage>> ExtractPagesCoreAsync(
        Stream document,
        string identifier,
        string mediaType,
        DocumentExtractionOptions? options,
        CancellationToken cancellationToken)
    {
        DocumentExtractionOptions normalizedOptions = ProviderAdapterHelpers.NormalizeOptions(options);
        ProviderAdapterHelpers.RejectUnsupportedProperties(
            normalizedOptions,
            nameof(FoundryMistralOcrClient),
            "includeImages",
            "imageLimit",
            "imageMinSize");
        string model = ProviderAdapterHelpers.ResolveModel(
            normalizedOptions,
            _defaultModel,
            nameof(FoundryMistralOcrClient));
        bool includeImages = ProviderAdapterHelpers.GetProperty<bool?>(
            normalizedOptions,
            "includeImages") ?? _includeImages;

        using var buffer = new MemoryStream();
        await document.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        string dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(buffer.ToArray())}";

        var requestBody = new JsonObject
        {
            ["model"] = model,
            ["document"] = new JsonObject
            {
                ["type"] = "document_url",
                ["document_url"] = dataUrl,
            },
            ["include_image_base64"] = includeImages,
        };
        if (ProviderAdapterHelpers.GetProperty<int?>(normalizedOptions, "imageLimit") is int imageLimit)
        {
            if (imageLimit < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "imageLimit must be non-negative.");
            }

            requestBody["image_limit"] = imageLimit;
        }

        if (ProviderAdapterHelpers.GetProperty<int?>(normalizedOptions, "imageMinSize") is int imageMinSize)
        {
            if (imageMinSize < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "imageMinSize must be non-negative.");
            }

            requestBody["image_min_size"] = imageMinSize;
        }

        byte[] responseBytes = await SendAsync(requestBody, cancellationToken).ConfigureAwait(false);
        using JsonDocument payload = JsonDocument.Parse(responseBytes);
        JsonElement detachedPayload = payload.RootElement.Clone();
        DocumentSourceOutput sourceOutput = ProviderAdapterHelpers.CreateSourceOutput(
            identifier,
            ProviderName,
            model,
            normalizedOptions,
            detachedPayload,
            responseBytes);

        if (!payload.RootElement.TryGetProperty("pages", out JsonElement responsePages)
            || responsePages.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Mistral OCR response did not contain a pages array.");
        }

        var pages = new List<DocumentExtractionPage>();
        foreach (JsonElement page in responsePages.EnumerateArray())
        {
            int index = page.GetProperty("index").GetInt32();
            int pageNumber = checked(index + 1);
            string markdown = page.TryGetProperty("markdown", out JsonElement markdownElement)
                && markdownElement.ValueKind == JsonValueKind.String
                    ? markdownElement.GetString() ?? string.Empty
                    : string.Empty;

            (double? width, double? height) = GetDimensions(page);
            (DocumentSourcePage sourcePage, DocumentPageReference pageReference) =
                ProviderAdapterHelpers.CreatePageFrame(
                    identifier,
                    ProviderName,
                    pageNumber,
                    width,
                    height,
                    width.HasValue ? DocumentCoordinateUnit.Pixel : null,
                    width.HasValue ? DocumentCoordinateOrigin.TopLeft : null,
                    sourceOutput);
            var observations = new List<DocumentExtractionObservation>();
            var replacements = new List<(int Start, int Length, DocumentExtractionTable Table)>();
            var unplacedTables = new List<DocumentExtractionTable>();

            if (page.TryGetProperty("tables", out JsonElement tables)
                && tables.ValueKind == JsonValueKind.Array)
            {
                int tableIndex = 0;
                foreach (JsonElement table in tables.EnumerateArray())
                {
                    string? tableMarkdown = GetString(table, "content") ?? GetString(table, "markdown");
                    string? tableId = GetString(table, "id");
                    string? format = GetString(table, "format")
                        ?? (table.TryGetProperty("content", out _) ? null : "markdown");
                    (int Start, int Length)? match = string.IsNullOrEmpty(tableId)
                        ? null
                        : FindUnique(markdown, $"[{tableId}]({tableId})");
                    string placement = match.HasValue ? "provider-link" : "unplaced";
                    if (match is null && !string.IsNullOrEmpty(tableMarkdown))
                    {
                        match = FindUnique(markdown, tableMarkdown);
                        if (match.HasValue)
                        {
                            placement = "inline-content";
                        }
                    }

                    if (match is { } candidate && replacements.Any(replacement =>
                        replacement.Start < candidate.Start + candidate.Length
                        && candidate.Start < replacement.Start + replacement.Length))
                    {
                        match = null;
                        placement = "unplaced";
                    }

                    var tableAnnotation = new DocumentAnnotation(
                        "table",
                        ProviderAdapterHelpers.StructuredValue(
                            ("id", tableId),
                            ("format", format),
                            ("placement", placement),
                            ("cellStatus", "unavailable-from-provider")),
                        sourceOutput: sourceOutput,
                        rawRepresentation: ProviderAdapterHelpers.RetainRawRepresentation(sourceOutput, table.Clone()));
                    var extractionTable = new DocumentExtractionTable(
                        $"mistral:{index}:table:{tableIndex}",
                        tableIndex,
                        tableMarkdown ?? "[table]",
                        0,
                        0,
                        [],
                        [pageReference],
                        annotations: [tableAnnotation]);
                    if (match is { } position)
                    {
                        replacements.Add((position.Start, position.Length, extractionTable));
                    }
                    else
                    {
                        unplacedTables.Add(extractionTable);
                    }

                    tableIndex++;
                }
            }

            int cursor = 0;
            foreach ((int start, int length, DocumentExtractionTable table) in
                replacements.OrderBy(item => item.Start))
            {
                AddText(markdown[cursor..start]);
                observations.Add(new DocumentExtractionTable(
                    table.ProviderId,
                    observations.Count,
                    table.Markdown,
                    table.RowCount,
                    table.ColumnCount,
                    table.Cells,
                    table.PageReferences,
                    annotations: table.Annotations));
                cursor = start + length;
            }

            AddText(markdown[cursor..]);
            observations.AddRange(unplacedTables.Select(table => new DocumentExtractionTable(
                table.ProviderId,
                observations.Count,
                table.Markdown,
                table.RowCount,
                table.ColumnCount,
                table.Cells,
                table.PageReferences,
                annotations: table.Annotations)));

            if (page.TryGetProperty("images", out JsonElement images)
                && images.ValueKind == JsonValueKind.Array)
            {
                int imageIndex = 0;
                foreach (JsonElement image in images.EnumerateArray())
                {
                    ReadOnlyMemory<byte>? content = DecodeImage(image);
                    DocumentRegion? region = CreateImageRegion(sourcePage, image);
                    var annotation = new DocumentAnnotation(
                        "figure",
                        regions: region is null ? null : [region],
                        sourceOutput: sourceOutput,
                        rawRepresentation: ProviderAdapterHelpers.RetainRawRepresentation(sourceOutput, image.Clone()));
                    observations.Add(new DocumentExtractionImage(
                        $"mistral:{index}:image:{imageIndex++}",
                        observations.Count,
                        "[image]",
                        content: content,
                        mediaType: content is null ? null : "image/png",
                        pageReferences: [pageReference],
                        annotations: [annotation]));
                }
            }

            pages.Add(new DocumentExtractionPage(
                pageNumber,
                observations,
                markdown,
                [pageReference],
                sourcePage,
                sourceOutputs: [sourceOutput]));

            void AddText(string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    observations.Add(new DocumentExtractionText(
                        $"mistral:{index}:text:{observations.Count}",
                        observations.Count,
                        DocumentTextKind.Paragraph,
                        value,
                        value,
                        pageReferences: [pageReference],
                        annotations:
                        [
                            new DocumentAnnotation(
                                "text",
                                sourceOutput: sourceOutput,
                                rawRepresentation: ProviderAdapterHelpers.RetainRawRepresentation(sourceOutput, page.Clone())),
                        ]));
                }
            }
        }

        return pages;
    }

    private async Task<byte[]> SendAsync(JsonObject body, CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(s_scope, cancellationToken)
            .ConfigureAwait(false);
        string url = $"{_endpoint.ToString().TrimEnd('/')}/providers/mistral/azure/ocr";
        string json = body.ToJsonString();

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            byte[] responseBytes = await response.Content
                .ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 5)
            {
                await _delayAsync(TimeSpan.FromSeconds(attempt * 3), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Mistral OCR returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            return responseBytes;
        }

        throw new HttpRequestException("Mistral OCR retries were exhausted.");
    }

    private static (double? Width, double? Height) GetDimensions(JsonElement page)
    {
        if (page.TryGetProperty("dimensions", out JsonElement dimensions)
            && dimensions.ValueKind == JsonValueKind.Object
            && dimensions.TryGetProperty("width", out JsonElement width)
            && dimensions.TryGetProperty("height", out JsonElement height)
            && width.TryGetDouble(out double widthValue)
            && height.TryGetDouble(out double heightValue))
        {
            return (widthValue, heightValue);
        }

        return (null, null);
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static ReadOnlyMemory<byte>? DecodeImage(JsonElement image)
    {
        if (!image.TryGetProperty("image_base64", out JsonElement base64)
            || base64.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string raw = base64.GetString()!;
        int comma = raw.IndexOf(',');
        return Convert.FromBase64String(comma >= 0 ? raw[(comma + 1)..] : raw);
    }

    private static DocumentRegion? CreateImageRegion(
        DocumentSourcePage sourcePage,
        JsonElement image)
    {
        if (!TryCoordinate(image, "top_left_x", out float left)
            || !TryCoordinate(image, "top_left_y", out float top)
            || !TryCoordinate(image, "bottom_right_x", out float right)
            || !TryCoordinate(image, "bottom_right_y", out float bottom))
        {
            return null;
        }

        return ProviderAdapterHelpers.CreateRegion(
            sourcePage,
            [left, top, right, top, right, bottom, left, bottom]);
    }

    private static bool TryCoordinate(JsonElement element, string propertyName, out float value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.TryGetSingle(out value);
    }

    public static (int Start, int Length)? FindUnique(string source, string value)
    {
        int start = source.IndexOf(value, StringComparison.Ordinal);
        return start >= 0
            && source.IndexOf(value, start + value.Length, StringComparison.Ordinal) < 0
                ? (start, value.Length)
                : null;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
