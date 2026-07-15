using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

using Microsoft.Extensions.AI;

namespace DemoOcr;

/// <summary>
/// An <see cref="IOcrClient"/> backed by Mistral OCR on Azure AI Foundry — a purpose-built
/// document-AI model. Document-native archetype: the whole PDF goes up in one call and comes back
/// as an ordered list of pages (per-page Markdown + tables), no client-side page splitting.
///
/// Keyless (Entra ID) via any <see cref="TokenCredential"/>. The Foundry route is vendor-namespaced:
/// <c>{endpoint}/providers/mistral/azure/ocr</c> and accepts a base64 data URL (no public-URL fetch).
/// </summary>
public sealed class FoundryMistralOcrClient(
    Uri endpoint,
    TokenCredential credential,
    string defaultModel = "mistral-ocr-4-0") : IOcrClient
{
    private static readonly TokenRequestContext s_scope =
        new(["https://cognitiveservices.azure.com/.default"]);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<OcrResult> ExtractAsync(
        Stream document,
        string mediaType,
        OcrOptions? options = null,
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
            ["include_image_base64"] = options?.IncludeImages ?? false,
        };

        AccessToken token = await credential.GetTokenAsync(s_scope, cancellationToken).ConfigureAwait(false);
        _http.DefaultRequestHeaders.Authorization = new("Bearer", token.Token);
        var url = $"{endpoint.ToString().TrimEnd('/')}/providers/mistral/azure/ocr";

        // Cold-start / capacity returns transient 503 (code 3700). Retrying is exactly the
        // cross-cutting concern IOcrClient middleware (a DelegatingOcrClient) is designed to own.
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

        var pages = new List<OcrPage>();
        JsonElement pageArray = root.GetProperty("pages");
        int total = pageArray.GetArrayLength();
        foreach (JsonElement page in pageArray.EnumerateArray())
        {
            int index = page.GetProperty("index").GetInt32();
            string markdown = page.TryGetProperty("markdown", out var md) ? md.GetString() ?? "" : "";
            int tableCount = page.TryGetProperty("tables", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.GetArrayLength() : 0;
            var tables = new List<OcrTable>(tableCount);
            for (int i = 0; i < tableCount; i++)
            {
                tables.Add(new OcrTable(0, 0)); // Mistral reports tables inline in the page markdown.
            }

            // Figures: Mistral returns page.images[] with a bbox and (when include_image_base64=true) the
            // rendered bytes. This is the document-native archetype filling OcrImage.Content + bbox.
            var images = new List<OcrImage>();
            if (page.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement img in imgs.EnumerateArray())
                {
                    var image = new OcrImage();
                    if (img.TryGetProperty("image_base64", out var b64) && b64.ValueKind == JsonValueKind.String)
                    {
                        string raw = b64.GetString()!;
                        image.Content = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            ? new DataContent(raw)
                            : new DataContent(Convert.FromBase64String(raw), "image/png");
                    }

                    if (img.TryGetProperty("top_left_x", out var tlx) && img.TryGetProperty("top_left_y", out var tly)
                        && img.TryGetProperty("bottom_right_x", out var brx) && img.TryGetProperty("bottom_right_y", out var bry))
                    {
                        image.BoundingRegion = OcrBoundingRegion.FromRectangle(
                            index + 1, tlx.GetDouble(), tly.GetDouble(), brx.GetDouble(), bry.GetDouble());
                    }

                    images.Add(image);
                }
            }

            pages.Add(new OcrPage(index + 1, markdown) { Tables = tables, Images = images });
        }

        return new OcrResult(pages)
        {
            ModelId = root.TryGetProperty("model", out var m) ? m.GetString() : model,
            Usage = new OcrUsage { PagesProcessed = total },
            RawRepresentation = root.Clone(),
        };
    }

    public IAsyncEnumerable<OcrResponseUpdate> ExtractStreamingAsync(
        Stream document, string mediaType, OcrOptions? options = null, CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();
}
