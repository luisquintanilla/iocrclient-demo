using System.Collections.ObjectModel;
using Azure;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

internal static class ProviderAdapterHelpers
{
    public static DocumentExtractionOptions NormalizeOptions(DocumentExtractionOptions? options)
        => options?.Clone() ?? new DocumentExtractionOptions();

    public static void RejectUnsupportedProperties(
        DocumentExtractionOptions options,
        string providerName,
        params string[] supportedProperties)
    {
        var supported = new HashSet<string>(supportedProperties, StringComparer.OrdinalIgnoreCase);
        var unsupported = options.AdditionalProperties.Keys
            .Where(key => !supported.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (unsupported.Length > 0)
        {
            throw new NotSupportedException(
                $"{providerName} does not support extraction option(s): {string.Join(", ", unsupported)}.");
        }
    }

    public static string ResolveModel(
        DocumentExtractionOptions options,
        string defaultModel,
        string providerName,
        bool allowOverride = true)
    {
        if (string.IsNullOrWhiteSpace(options.ModelId))
        {
            return defaultModel;
        }

        if (!allowOverride && !string.Equals(options.ModelId, defaultModel, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"{providerName} is configured for model '{defaultModel}' and does not support per-request model overrides.");
        }

        return options.ModelId;
    }

    public static T? GetProperty<T>(DocumentExtractionOptions options, string name)
    {
        if (!options.AdditionalProperties.TryGetValue(name, out var value))
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        throw new ArgumentException(
            $"Extraction option '{name}' must be a {typeof(T).Name}, but received {value?.GetType().Name ?? "null"}.",
            nameof(options));
    }

    public static DocumentSourceOutput CreateSourceOutput(
        string identifier,
        string providerName,
        string modelId,
        DocumentExtractionOptions options,
        object? nativeRepresentation,
        byte[]? exactResponseBytes = null,
        string exactResponseMediaType = "application/json")
    {
        var captureDisabled = !options.CaptureRawOutput;
        DocumentArtifact? rawContent = captureDisabled || exactResponseBytes is null
            ? null
            : new DocumentArtifact(
                exactResponseMediaType,
                new MemoryStream(exactResponseBytes, writable: false));
        return new DocumentSourceOutput(
            $"{identifier}:{providerName}:output",
            captureDisabled ? null : nativeRepresentation,
            rawContent,
            captureDisabled,
            providerName,
            modelId);
    }

    public static byte[]? GetBufferedResponseBytes(Response response)
    {
        Stream? stream = response.ContentStream;
        if (stream is null || !stream.CanSeek || !stream.CanRead)
        {
            return null;
        }

        long originalPosition = stream.Position;
        stream.Position = 0;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        stream.Position = originalPosition;
        return buffer.Length > 0 ? buffer.ToArray() : null;
    }

    public static (DocumentSourcePage SourcePage, DocumentPageReference Reference) CreatePageFrame(
        string identifier,
        string providerName,
        int pageNumber,
        double? width,
        double? height,
        DocumentCoordinateUnit? unit,
        DocumentCoordinateOrigin? origin,
        DocumentSourceOutput sourceOutput)
    {
        DocumentSourcePage sourcePage = new(
            $"{identifier}:{providerName}:page:{pageNumber}",
            pageNumber,
            width.HasValue && height.HasValue
                ? new DocumentPageDimensions(width.Value, height.Value)
                : null,
            unit,
            origin,
            sourceOutput);

        return (sourcePage, new DocumentPageReference(sourcePage.Identifier));
    }

    public static DocumentRegion? CreateRegion(
        DocumentSourcePage? sourcePage,
        IEnumerable<float>? coordinates)
    {
        if (sourcePage is null || coordinates is null)
        {
            return null;
        }

        var values = coordinates.ToArray();
        if (values.Length < 6 || values.Length % 2 != 0)
        {
            return null;
        }

        var points = new List<DocumentPoint>(values.Length / 2);
        for (var index = 0; index < values.Length; index += 2)
        {
            points.Add(new DocumentPoint(values[index], values[index + 1]));
        }

        return new DocumentRegion(sourcePage, points);
    }

    public static DocumentRegion? CreateRegion(
        DocumentSourcePage? sourcePage,
        IEnumerable<System.Drawing.PointF>? points)
    {
        if (sourcePage is null || points is null)
        {
            return null;
        }

        DocumentPoint[] polygon = points
            .Select(point => new DocumentPoint(point.X, point.Y))
            .ToArray();
        return polygon.Length >= 3 ? new DocumentRegion(sourcePage, polygon) : null;
    }

    public static DocumentConfidence? CreateConfidence(float? confidence)
        => confidence.HasValue
            ? new DocumentConfidence(confidence.Value, "provider")
            : null;

    public static IReadOnlyDictionary<string, object?> StructuredValue(params (string Key, object? Value)[] values)
        => new ReadOnlyDictionary<string, object?>(
            values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal));
}
