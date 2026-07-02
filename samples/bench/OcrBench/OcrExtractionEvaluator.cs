using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace OcrBench;

/// <summary>
/// A CUSTOM, fully deterministic evaluator (no LLM judge) that scores how much STRUCTURE an OCR/extraction
/// pass recovered from a document. It answers the question naive text extraction cannot: "did we capture
/// the tables, figures, and buried facts, or only the flowing text?" Reads an <see cref="OcrStructuralContext"/>
/// from <c>additionalContext</c> and emits four metrics:
/// <list type="bullet">
/// <item><b>ExtractionYield</b> — characters of text recovered (raw volume).</item>
/// <item><b>TablesDetected</b> — number of tables the engine surfaced (naive PdfPig = 0).</item>
/// <item><b>ImagesDetected</b> — number of images/figures the engine surfaced (naive PdfPig = 0).</item>
/// <item><b>KnownFactCoverage</b> — fraction in [0,1] of the authored known facts found in the text
///   (reference-free: no gold answer, just "is the fact present at all").</item>
/// </list>
/// This is the point of the eval: OCR engines should out-score naive PdfPig on tables/figures/coverage
/// for a table-and-figure-rich document, and the numbers are reproducible because the evaluator is
/// deterministic. Being an <see cref="IEvaluator"/> means it drops into the same reporting pipeline as
/// the built-in evaluators.
/// </summary>
public sealed class OcrExtractionEvaluator : IEvaluator
{
    public const string YieldMetric = "ExtractionYield";
    public const string TablesMetric = "TablesDetected";
    public const string ImagesMetric = "ImagesDetected";
    public const string CoverageMetric = "KnownFactCoverage";

    public IReadOnlyCollection<string> EvaluationMetricNames { get; } =
        [YieldMetric, TablesMetric, ImagesMetric, CoverageMetric];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        if (additionalContext?.OfType<OcrStructuralContext>().FirstOrDefault() is not { } ctx)
        {
            throw new InvalidOperationException(
                $"A value of type '{nameof(OcrStructuralContext)}' was not found in the '{nameof(additionalContext)}' collection.");
        }

        int matched = ctx.KnownFacts.Count(f =>
            !string.IsNullOrWhiteSpace(f) &&
            ctx.ExtractedText.Contains(f, StringComparison.OrdinalIgnoreCase));
        double coverage = ctx.KnownFacts.Count == 0 ? 0d : (double)matched / ctx.KnownFacts.Count;

        var result = new EvaluationResult(
            new NumericMetric(YieldMetric, ctx.ExtractedText.Length,
                reason: $"{ctx.ExtractedText.Length} characters over {ctx.PageCount} page(s)."),
            new NumericMetric(TablesMetric, ctx.TableCount,
                reason: $"{ctx.TableCount} table(s) surfaced by the engine."),
            new NumericMetric(ImagesMetric, ctx.ImageCount,
                reason: $"{ctx.ImageCount} image(s)/figure(s) surfaced by the engine."),
            new NumericMetric(CoverageMetric, coverage,
                reason: $"{matched} of {ctx.KnownFacts.Count} known facts found in the extracted text."));

        return new ValueTask<EvaluationResult>(result);
    }
}
