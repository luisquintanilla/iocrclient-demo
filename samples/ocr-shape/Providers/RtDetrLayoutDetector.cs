using System.Security.Cryptography;

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using SkiaSharp;

namespace DemoOcr;

/// <summary>
/// Runs the Apache-2.0 Docling Layout Heron RT-DETR v2 ONNX export on CPU.
/// </summary>
public sealed class RtDetrLayoutDetector : IDisposable
{
    public const string PublishedSha256 =
        "3bf2fb0ee6df87435b7ae47f0f3930ec3dc97ec56fd824acc6d57bc7a6b89ef2";

    public const string ModelRevision =
        "c6bf493e2f7b0b9a29a5870da9880c14e20ff0a3";

    public static readonly Uri ModelSource =
        new($"https://huggingface.co/xberg-io/layout-models/resolve/{ModelRevision}/rtdetr/model.onnx");

    public static readonly IReadOnlyList<string> DefaultLabels =
    [
        "Caption",
        "Footnote",
        "Formula",
        "ListItem",
        "PageFooter",
        "PageHeader",
        "Picture",
        "SectionHeader",
        "Table",
        "Text",
        "Title",
        "DocumentIndex",
        "Code",
        "CheckboxSelected",
        "CheckboxUnselected",
        "Form",
        "KeyValueRegion",
    ];

    private readonly InferenceSession _session;
    private readonly IReadOnlyList<string> _labels;

    public RtDetrLayoutDetector(
        string modelPath,
        float confidenceThreshold = 0.5f,
        string expectedSha256 = PublishedSha256,
        IReadOnlyList<string>? labels = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("A model path is required.", nameof(modelPath));
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                "The RT-DETR model file is required for explicit layoutMode 'rtdetr'.",
                modelPath);
        }

        if (confidenceThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confidenceThreshold),
                "Confidence threshold must be in [0,1].");
        }

        string actualSha256 = Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(modelPath)));
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"RT-DETR model SHA-256 mismatch. Expected {expectedSha256}; got {actualSha256}.");
        }

        ConfidenceThreshold = confidenceThreshold;
        _labels = labels ?? DefaultLabels;
        if (_labels.Count == 0)
        {
            throw new ArgumentException("At least one layout label is required.", nameof(labels));
        }

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, sessionOptions);
        ValidateGraph(_session);
    }

    public float ConfidenceThreshold { get; }

    public IReadOnlyList<LayoutDetection> Detect(
        PdfRasterizedPage page,
        float? confidenceThreshold = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        float threshold = confidenceThreshold ?? ConfidenceThreshold;
        if (threshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceThreshold));
        }

        cancellationToken.ThrowIfCancellationRequested();
        DenseTensor<float> imageTensor = CreateImageTensor(page.PngBytes);
        var originalSizeTensor = new DenseTensor<long>(new[] { 1, 2 });
        originalSizeTensor[0, 0] = page.Width;
        originalSizeTensor[0, 1] = page.Height;

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("images", imageTensor),
            NamedOnnxValue.CreateFromTensor("orig_target_sizes", originalSizeTensor),
        ]);
        cancellationToken.ThrowIfCancellationRequested();

        Tensor<long> labels = GetOutput(outputs, "labels").AsTensor<long>();
        Tensor<float> boxes = GetOutput(outputs, "boxes").AsTensor<float>();
        Tensor<float> scores = GetOutput(outputs, "scores").AsTensor<float>();
        int count = checked((int)Math.Min(labels.Length, Math.Min(scores.Length, boxes.Length / 4)));
        var detections = new List<LayoutDetection>(count);
        for (int index = 0; index < count; index++)
        {
            float score = scores.GetValue(index);
            if (score < threshold)
            {
                continue;
            }

            int labelId = checked((int)labels.GetValue(index));
            if ((uint)labelId >= (uint)_labels.Count)
            {
                throw new InvalidDataException(
                    $"RT-DETR returned label {labelId}, outside the configured label range.");
            }

            double left = Math.Clamp(boxes.GetValue(index * 4), 0, page.Width);
            double top = Math.Clamp(boxes.GetValue(index * 4 + 1), 0, page.Height);
            double right = Math.Clamp(boxes.GetValue(index * 4 + 2), 0, page.Width);
            double bottom = Math.Clamp(boxes.GetValue(index * 4 + 3), 0, page.Height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            detections.Add(new LayoutDetection(
                labelId,
                _labels[labelId],
                score,
                new PixelBox(left, top, right, bottom)));
        }

        return detections
            .OrderBy(detection => detection.Box.Top)
            .ThenBy(detection => detection.Box.Left)
            .ThenByDescending(detection => detection.Score)
            .ToArray();
    }

    private static DenseTensor<float> CreateImageTensor(byte[] pngBytes)
    {
        using SKBitmap decoded = SKBitmap.Decode(pngBytes)
            ?? throw new InvalidDataException("The rendered PDF page is not a valid PNG.");
        using var resized = new SKBitmap(
            new SKImageInfo(640, 640, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.White);
            using SKImage decodedImage = SKImage.FromBitmap(decoded);
            canvas.DrawImage(
                decodedImage,
                new SKRect(0, 0, 640, 640),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None),
                paint: null);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, 640, 640 });
        for (int y = 0; y < 640; y++)
        {
            for (int x = 0; x < 640; x++)
            {
                SKColor color = resized.GetPixel(x, y);
                tensor[0, 0, y, x] = color.Red / 255f;
                tensor[0, 1, y, x] = color.Green / 255f;
                tensor[0, 2, y, x] = color.Blue / 255f;
            }
        }

        return tensor;
    }

    private static DisposableNamedOnnxValue GetOutput(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string name)
        => outputs.FirstOrDefault(output => string.Equals(output.Name, name, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"RT-DETR graph did not return output '{name}'.");

    private static void ValidateGraph(InferenceSession session)
    {
        ValidateNode(session.InputMetadata, "images", TensorElementType.Float, [1, 3, 640, 640]);
        ValidateNode(session.InputMetadata, "orig_target_sizes", TensorElementType.Int64, [1, 2]);
        ValidateNode(session.OutputMetadata, "labels", TensorElementType.Int64);
        ValidateNode(session.OutputMetadata, "boxes", TensorElementType.Float);
        ValidateNode(session.OutputMetadata, "scores", TensorElementType.Float);
    }

    private static void ValidateNode(
        IReadOnlyDictionary<string, NodeMetadata> metadata,
        string name,
        TensorElementType elementType,
        int[]? expectedDimensions = null)
    {
        if (!metadata.TryGetValue(name, out NodeMetadata? node))
        {
            throw new InvalidDataException($"RT-DETR graph is missing node '{name}'.");
        }

        if (node.ElementDataType != elementType)
        {
            throw new InvalidDataException(
                $"RT-DETR node '{name}' must be {elementType}, but is {node.ElementDataType}.");
        }

        if (expectedDimensions is not null
            && (node.Dimensions.Length != expectedDimensions.Length
                || node.Dimensions
                    .Zip(expectedDimensions)
                    .Any(pair => pair.First != -1 && pair.First != pair.Second)))
        {
            throw new InvalidDataException(
                $"RT-DETR node '{name}' dimensions must be [{string.Join(",", expectedDimensions)}], " +
                $"but are [{string.Join(",", node.Dimensions)}].");
        }
    }

    public void Dispose() => _session.Dispose();
}

public sealed record LayoutDetection(
    int LabelId,
    string Label,
    float Score,
    PixelBox Box);
