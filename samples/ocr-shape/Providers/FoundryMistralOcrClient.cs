using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;

namespace DemoOcr;

/// <summary>
/// An <see cref="IDocumentExtractionClient"/> backed by Mistral OCR on Azure AI Foundry — a purpose-built
/// document-AI model. Document-native archetype: the whole PDF goes up in one call and comes back
/// as an ordered list of pages with exact provider Markdown and optional images, no client-side page
/// splitting. Markdown is not copied into canonical elements.
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
            // Figures: Mistral returns page.images[] with a bbox and (when include_image_base64=true) the
            // rendered bytes. This is the document-native archetype filling DocumentImage.Content + bbox.
            var images = new List<DocumentImage>();
            if (page.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement img in imgs.EnumerateArray())
                {
                    var image = new DocumentImage();
                    if (img.TryGetProperty("image_base64", out var b64) && b64.ValueKind == JsonValueKind.String)
                    {
                        string raw = b64.GetString()!;
                        int comma = raw.IndexOf(',');
                        image.Content = Convert.FromBase64String(comma >= 0 ? raw[(comma + 1)..] : raw);
                        image.MediaType = raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                            ? raw[5..raw.IndexOf(';')]
                            : "image/png";
                    }

                    if (img.TryGetProperty("top_left_x", out var tlx) && img.TryGetProperty("top_left_y", out var tly)
                        && img.TryGetProperty("bottom_right_x", out var brx) && img.TryGetProperty("bottom_right_y", out var bry))
                    {
                        image.BoundingRegion = DocumentBoundingRegion.FromRectangle(
                            index + 1, (float)tlx.GetDouble(), (float)tly.GetDouble(), (float)brx.GetDouble(), (float)bry.GetDouble());
                    }

                    images.Add(image);
                }
            }

            pages.Add(new DocumentPage(index + 1, images, markdown));
        }

        return new DocumentExtractionResult(pages)
        {
            RawRepresentation = root.Clone(),
            AdditionalProperties = new() { ["modelId"] = root.TryGetProperty("model", out var m) ? m.GetString() ?? model : model },
        };
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document, string mediaType, DocumentExtractionOptions? options = null, CancellationToken cancellationToken = default)
        => OcrShapeExtensions.StreamAsUpdates(ct => ExtractAsync(document, mediaType, options, ct), cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _http.Dispose();
}
