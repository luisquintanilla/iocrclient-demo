using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by Mistral OCR on Azure AI Foundry — a purpose-built
/// document-AI model. Document-native archetype: the whole PDF goes up in one call and comes back
/// as an ordered list of pages (per-page Markdown + tables), no client-side page splitting.
///
/// Keyless (Entra ID) via any <see cref="TokenCredential"/>. The Foundry route is vendor-namespaced:
/// <c>{endpoint}/providers/mistral/azure/ocr</c> and accepts a base64 data URL (no public-URL fetch).
/// </summary>
public sealed class FoundryMistralOcrClient(
    Uri endpoint,
    TokenCredential credential,
    string defaultModel = "mistral-ocr-4-0") : IDocumentExtractionClient
{
    private static readonly TokenRequestContext s_scope =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<DocumentExtractionResult> ExtractAsync(
        Stream document,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await document.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        string dataUrl = $"data:{mediaType};base64,{Convert.ToBase64String(ms.ToArray())}";
        string model = options?.ModelId ?? defaultModel;

        var request = new JsonObject
        {
            ["model"] = model,
            ["document"] = new JsonObject { ["type"] = "document_url", ["document_url"] = dataUrl },
            ["include_image_base64"] = options.GetIncludeImages(),
        };

        AccessToken token = await credential.GetTokenAsync(s_scope, cancellationToken).ConfigureAwait(false);
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        var url = $"{endpoint.ToString().TrimEnd('/')}/providers/mistral/azure/ocr";

        // Cold-start / capacity returns transient 503 (code 3700). Retrying is exactly the
        // cross-cutting concern IDocumentExtractionClient middleware (a DelegatingOcrClient) is designed to own.
        HttpResponseMessage resp = null!;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            resp = await _http.PostAsync(
                url,
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
                cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(attempt * 3), cancellationToken).ConfigureAwait(false);
        }

        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(
            await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = doc.RootElement;

        var pages = new List<DocumentPage>();
        JsonElement pageArray = root.GetProperty("pages");
        int total = pageArray.GetArrayLength();
        foreach (JsonElement page in pageArray.EnumerateArray())
        {
            int index = page.GetProperty("index").GetInt32();
            string markdown = page.TryGetProperty("markdown", out var md) ? md.GetString() ?? "" : "";
            int pageNumber = index + 1;
            var evidence = new List<DocumentExtractionEvidence>();
            var unplacedNodes = new List<DocumentNode>();
            bool hasImages = page.TryGetProperty("images", out JsonElement imgs) &&
                imgs.ValueKind == JsonValueKind.Array;
            var replacements = new List<(int Start, int Length, DocumentNode Node)>();

            if (page.TryGetProperty("tables", out JsonElement tables) &&
                tables.ValueKind == JsonValueKind.Array)
            {
                int tableIndex = 0;
                foreach (JsonElement providerTable in tables.EnumerateArray())
                {
                    int currentTableIndex = tableIndex++;
                    string? tableMarkdown = GetString(providerTable, "markdown")
                        ?? GetString(providerTable, "content");
                    DocumentTable? parsedTable = tableMarkdown is null
                        ? null
                        : CreateTable("mistral", pageNumber, currentTableIndex, tableMarkdown);
                    DocumentTable table = parsedTable ?? CreateTableShell(
                        "mistral", pageNumber, currentTableIndex, providerTable);
                    if (tableMarkdown is not null &&
                        FindUnique(markdown, tableMarkdown) is { } match &&
                        parsedTable is not null)
                    {
                        replacements.Add((match.Start, match.Length, table));
                    }
                    else
                    {
                        // The exact Markdown remains the semantic text authority when provider order
                        // cannot be established. Preserve table shape + raw evidence without duplicating text.
                        unplacedNodes.Add(new DocumentTable(
                            table.Id,
                            table.RowCount,
                            table.ColumnCount,
                            [],
                            pageReferences: table.PageReferences));
                    }

                    evidence.Add(new DocumentExtractionEvidence(table.Id)
                    {
                        RawRepresentation = providerTable.Clone(),
                    });
                }
            }

            if (hasImages)
            {
                int imageIndex = 0;
                foreach (JsonElement img in imgs.EnumerateArray())
                {
                    byte[] imageBytes = [];
                    string? imageMediaType = null;
                    if (img.TryGetProperty("image_base64", out var b64) && b64.ValueKind == JsonValueKind.String)
                    {
                        string raw = b64.GetString()!;
                        int comma = raw.IndexOf(',');
                        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                        {
                            string metadata = raw[5..comma];
                            int semicolon = metadata.IndexOf(';');
                            string declaredMediaType = semicolon >= 0 ? metadata[..semicolon] : metadata;
                            if (declaredMediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                            {
                                imageMediaType = declaredMediaType;
                            }
                        }
                        imageBytes = Convert.FromBase64String(
                            raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0
                                ? raw[(comma + 1)..]
                                : raw);
                        imageMediaType ??= DetectImageMediaType(imageBytes);
                    }

                    string? providerImageId = img.TryGetProperty("id", out JsonElement id)
                        ? id.GetString()
                        : null;
                    Match? marker = providerImageId is { Length: > 0 }
                        ? Regex.Match(
                            markdown,
                            $@"!\[(?<alt>[^\]]*)\]\(\s*{Regex.Escape(providerImageId)}\s*\)",
                            RegexOptions.CultureInvariant)
                        : null;
                    string? alt = marker?.Success == true ? marker.Groups["alt"].Value : null;
                    string? description = !string.IsNullOrWhiteSpace(alt) &&
                        !string.Equals(alt, providerImageId, StringComparison.OrdinalIgnoreCase)
                            ? alt
                            : null;
                    Uri? sourceUri = providerImageId is { Length: > 0 }
                        ? new Uri(providerImageId, UriKind.RelativeOrAbsolute)
                        : null;
                    if (imageBytes.Length == 0 && sourceUri is null && description is null)
                    {
                        continue;
                    }

                    DocumentNodeId nodeId = DocumentExtractionDemoExtensions.CreateNodeId(
                        "mistral", pageNumber, "image", imageIndex++);
                    DocumentImage imageNode = new(
                        nodeId,
                        imageBytes,
                        imageMediaType,
                        source: sourceUri,
                        description: description,
                        pageReferences: [new(pageNumber)]);
                    if (marker?.Success == true)
                    {
                        replacements.Add((marker.Index, marker.Length, imageNode));
                    }
                    else
                    {
                        unplacedNodes.Add(imageNode);
                    }

                    DocumentBoundingRegion? region = null;
                    if (img.TryGetProperty("top_left_x", out var tlx) && img.TryGetProperty("top_left_y", out var tly)
                        && img.TryGetProperty("bottom_right_x", out var brx) && img.TryGetProperty("bottom_right_y", out var bry)
                        && tlx.ValueKind == JsonValueKind.Number && tly.ValueKind == JsonValueKind.Number
                        && brx.ValueKind == JsonValueKind.Number && bry.ValueKind == JsonValueKind.Number)
                    {
                        region = DocumentBoundingRegion.FromRectangle(
                            pageNumber, (float)tlx.GetDouble(), (float)tly.GetDouble(), (float)brx.GetDouble(), (float)bry.GetDouble());
                    }

                    evidence.Add(new DocumentExtractionEvidence(nodeId)
                    {
                        BoundingRegion = region,
                        RawRepresentation = img.Clone(),
                        AdditionalProperties = providerImageId is { Length: > 0 }
                            ? new() { ["mistral.imageId"] = providerImageId }
                            : null,
                    });
                }
            }

            var nodes = new List<DocumentNode>();
            int cursor = 0;
            foreach ((int start, int length, DocumentNode node) in replacements
                .OrderBy(replacement => replacement.Start))
            {
                if (start < cursor)
                {
                    continue;
                }
                AddText(markdown[cursor..start]);
                nodes.Add(node);
                cursor = start + length;
            }
            AddText(markdown[cursor..]);
            nodes.AddRange(unplacedNodes);

            pages.Add(new DocumentPage(
                pageNumber,
                DocumentExtractionDemoExtensions.CreatePageDocument("mistral", pageNumber, nodes),
                markdown,
                evidence)
            {
                RawRepresentation = page.Clone(),
            });

            void AddText(string source)
            {
                string text = DocumentExtractionDemoExtensions.ProjectProviderMarkdown(source);
                if (text.Length > 0)
                {
                    nodes.Add(DocumentExtractionDemoExtensions.CreateTextNode(
                        "mistral", pageNumber, nodes.Count, text));
                }
            }
        }

        return new DocumentExtractionResult(pages)
        {
            Usage = new DocumentExtractionUsage { PagesProcessed = total },
            RawRepresentation = root.Clone(),
            AdditionalProperties = new() { ["modelId"] = root.TryGetProperty("model", out var m) ? m.GetString() ?? model : model },
        };
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => DocumentExtractionDemoExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static (int Start, int Length)? FindUnique(string source, string value)
    {
        int start = source.IndexOf(value, StringComparison.Ordinal);
        return start >= 0 &&
            source.IndexOf(value, start + value.Length, StringComparison.Ordinal) < 0
                ? (start, value.Length)
                : null;
    }

    private static DocumentTable? CreateTable(
        string provider,
        int pageNumber,
        int tableIndex,
        string markdown)
    {
        string[][] rows = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Contains('|') && !IsMarkdownTableSeparator(line))
            .Select(line => line.Trim('|', ' ').Split('|').Select(cell => cell.Trim()).ToArray())
            .ToArray();
        int columns = rows.Select(row => row.Length).DefaultIfEmpty().Max();
        if (rows.Length == 0 || columns == 0 || rows.Any(row => row.Length != columns))
        {
            return null;
        }

        var cells = new List<DocumentTableCell>();
        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int cellIndex = cells.Count;
                DocumentText text = new(
                    DocumentExtractionDemoExtensions.CreateNodeId(
                        provider, pageNumber, $"table-{tableIndex}-cell-text", cellIndex),
                    rows[row][column],
                    pageReferences: [new(pageNumber)]);
                cells.Add(new DocumentTableCell(
                    DocumentExtractionDemoExtensions.CreateNodeId(
                        provider, pageNumber, $"table-{tableIndex}-cell", cellIndex),
                    row,
                    column,
                    [text],
                    role: row == 0
                        ? DocumentTableCellRole.ColumnHeader
                        : DocumentTableCellRole.Content,
                    pageReferences: [new(pageNumber)]));
            }
        }
        return new DocumentTable(
            DocumentExtractionDemoExtensions.CreateNodeId(
                provider, pageNumber, "table", tableIndex),
            rows.Length,
            columns,
            cells,
            pageReferences: [new(pageNumber)]);
    }

    public static bool IsMarkdownTableSeparator(string line)
        => Regex.IsMatch(
            line,
            @"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\s*$",
            RegexOptions.CultureInvariant);

    private static DocumentTable CreateTableShell(
        string provider,
        int pageNumber,
        int tableIndex,
        JsonElement providerTable)
    {
        int rows = GetInt(providerTable, "row_count") ?? GetInt(providerTable, "rowCount") ?? 0;
        int columns = GetInt(providerTable, "column_count") ?? GetInt(providerTable, "columnCount") ?? 0;
        return new DocumentTable(
            DocumentExtractionDemoExtensions.CreateNodeId(
                provider, pageNumber, "table", tableIndex),
            rows,
            columns,
            [],
            pageReferences: [new(pageNumber)]);
    }

    private static int? GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property) &&
            property.TryGetInt32(out int value)
                ? value
                : null;

    private static string DetectImageMediaType(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }
        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
        {
            return "image/gif";
        }
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        {
            return "image/webp";
        }
        return "application/octet-stream";
    }
}
