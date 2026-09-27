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
public sealed class FoundryMistralOcrClient(
    Uri endpoint,
    TokenCredential credential,
    string defaultModel = "mistral-ocr-4-0",
    bool includeImages = false) : IDocumentExtractionClient
{
    private static readonly TokenRequestContext s_scope =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string mediaType,
        CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamPages(
            ct => ExtractPagesCoreAsync(document, mediaType, cancellationToken: ct),
            cancellationToken);

    private async Task<IReadOnlyList<DocumentExtractionPage>> ExtractPagesCoreAsync(
        Stream document,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        string dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(ms.ToArray())}";
        string model = defaultModel;

        var request = new JsonObject
        {
            ["model"] = model,
            ["document"] = new JsonObject { ["type"] = "document_url", ["document_url"] = dataUrl },
            ["include_image_base64"] = includeImages,
        };

        AccessToken token = await credential.GetTokenAsync(s_scope, cancellationToken).ConfigureAwait(false);
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        string url = $"{endpoint.ToString().TrimEnd('/')}/providers/mistral/azure/ocr";

        HttpResponseMessage? response = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            response?.Dispose();
            response = await _http.PostAsync(
                url,
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt * 3), cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            response!.EnsureSuccessStatusCode();
            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));

            var pages = new List<DocumentExtractionPage>();
            foreach (JsonElement page in payload.RootElement.GetProperty("pages").EnumerateArray())
            {
                int index = page.GetProperty("index").GetInt32();
                string markdown = page.TryGetProperty("markdown", out JsonElement md)
                    ? md.GetString() ?? string.Empty
                    : string.Empty;
                DocumentPageReference pageReference = new($"page:{index + 1}");
                var observations = new List<DocumentExtractionObservation>();
                var replacements = new List<(int Start, int Length, DocumentExtractionTable Table)>();
                var unplacedTables = new List<DocumentExtractionTable>();
                if (page.TryGetProperty("tables", out JsonElement tables)
                    && tables.ValueKind == JsonValueKind.Array)
                {
                    int tableIndex = 0;
                    foreach (JsonElement table in tables.EnumerateArray())
                    {
                        string? tableMarkdown = table.TryGetProperty("markdown", out JsonElement tableMd)
                            && tableMd.ValueKind == JsonValueKind.String
                            ? tableMd.GetString()
                            : null;
                        var extractionTable = new DocumentExtractionTable(
                            $"mistral:{index}:table:{tableIndex}",
                            tableIndex,
                            "[table]",
                            0,
                            0,
                            pageReferences: [pageReference]);
                        if (tableMarkdown is not null && FindUnique(markdown, tableMarkdown) is { } match)
                        {
                            replacements.Add((match.Start, match.Length, extractionTable));
                        }
                        else
                        {
                            // Ambiguous or absent Markdown cannot establish reading order. Keep the
                            // structural observation without replacing semantic text.
                            unplacedTables.Add(extractionTable);
                        }

                        tableIndex++;
                    }
                }

                int cursor = 0;
                foreach ((int start, int length, DocumentExtractionTable table) in replacements.OrderBy(static item => item.Start))
                {
                    if (start < cursor)
                    {
                        unplacedTables.Add(table);
                        continue;
                    }

                    AddText(markdown[cursor..start]);
                    observations.Add(table);
                    cursor = start + length;
                }

                AddText(markdown[cursor..]);
                observations.AddRange(unplacedTables);

                if (page.TryGetProperty("images", out JsonElement images)
                    && images.ValueKind == JsonValueKind.Array)
                {
                    int imageIndex = 0;
                    foreach (JsonElement image in images.EnumerateArray())
                    {
                        ReadOnlyMemory<byte>? content = null;
                        if (image.TryGetProperty("image_base64", out JsonElement base64)
                            && base64.ValueKind == JsonValueKind.String)
                        {
                            string raw = base64.GetString()!;
                            int comma = raw.IndexOf(',');
                            content = Convert.FromBase64String(comma >= 0 ? raw[(comma + 1)..] : raw);
                        }

                        observations.Add(new DocumentExtractionImage(
                            $"mistral:{index}:image:{imageIndex}",
                            observations.Count,
                            "[image]",
                            content: content,
                            mediaType: content is null ? null : "image/png",
                            pageReferences: [pageReference]));
                        imageIndex++;
                    }
                }

                pages.Add(new DocumentExtractionPage(
                    index + 1,
                    observations,
                    markdown,
                    [pageReference]));

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
                            pageReferences: [pageReference]));
                    }
                }
            }

            return pages;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public static (int Start, int Length)? FindUnique(string source, string value)
    {
        int start = source.IndexOf(value, StringComparison.Ordinal);
        return start >= 0 && source.IndexOf(value, start + value.Length, StringComparison.Ordinal) < 0
            ? (start, value.Length)
            : null;
    }

    public void Dispose() => _http.Dispose();
}
