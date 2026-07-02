using Microsoft.Extensions.AI.Evaluation;

namespace OcrBench;

/// <summary>
/// Carries an extraction's structural facts to <see cref="OcrExtractionEvaluator"/> via the standard
/// <c>additionalContext</c> channel. This is how a deterministic evaluator receives data beyond the
/// chat transcript — the same mechanism the built-in NLP/Quality evaluators use for their own context.
/// </summary>
public sealed class OcrStructuralContext(
    string extractedText,
    int pageCount,
    int tableCount,
    int imageCount,
    IReadOnlyList<string> knownFacts)
    : EvaluationContext(name: "OCR Structural Extraction", content: extractedText)
{
    public string ExtractedText { get; } = extractedText;
    public int PageCount { get; } = pageCount;
    public int TableCount { get; } = tableCount;
    public int ImageCount { get; } = imageCount;
    public IReadOnlyList<string> KnownFacts { get; } = knownFacts;
}
