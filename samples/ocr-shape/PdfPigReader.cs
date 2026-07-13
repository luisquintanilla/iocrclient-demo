using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace DemoOcr;

// WHEN to OCR — a policy, NOT a model choice. Mirrors CommunityToolkit/AI #14's OcrPolicy.
public enum OcrPolicy
{
    Never,                  // native PdfPig text only (the old TextOnly)
    FallbackForEmptyPages,  // native first; OCR only pages with no digital text (the old Hybrid)
    AllPages,               // OCR the whole document via the injected IOcrClient (document-native)
}

// Per-page telemetry the OCR predicate sees: policy in the caller's hands, mechanism in the reader.
public readonly record struct PageOcrContext(int PageNumber, int NativeElementCount)
{
    public bool HasNativeText => NativeElementCount > 0;
}

// A PdfPig-backed MEDI reader with TWO clean pluggable seams. This is CommunityToolkit/AI PR #14 in
// miniature: native text first, OCR composed in as an injected enrichment (not bolted on downstream),
// depending on IOcrClient only. OCR is an injected capability, so the reader keeps the plain
// PdfPigReader name — under OcrPolicy.Never it is a pure native-text reader. Stamps the same metadata
// keys (page_number/ocr_source) as the other readers, so per-page chunking carries provenance into
// chunks identically.
//   seam 1 — IPageSegmenter: HOW to segment a page (DefaultPageSegmenter heuristic; swap in
//            OnnxPageSegmenter from CommunityToolkit/AI PR 3's PdfPig.OnnxLayoutAnalysis for ML layout).
//   seam 2 — IOcrClient + OcrPolicy: WHEN/whether to OCR (Never / FallbackForEmptyPages / AllPages).
public sealed class PdfPigReader(
    IOcrClient? ocrClient = null,
    OcrPolicy policy = OcrPolicy.Never,
    IPageSegmenter? pageSegmenter = null,
    Func<PageOcrContext, bool>? ocrPagePredicate = null) : IngestionDocumentReader
{
    public int OcrCalls { get; private set; }

    public override async Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        if (policy != OcrPolicy.Never && ocrClient is null)
            throw new ArgumentNullException(nameof(ocrClient), $"An {nameof(IOcrClient)} is required when policy is {policy}.");

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var document = new IngestionDocument(identifier);

        // Document-native archetype: one whole-document OCR call, its pages[] become sections.
        if (policy == OcrPolicy.AllPages && ocrClient is not null)
        {
            using var docStream = new MemoryStream(bytes, writable: false);
            OcrResult result = await ocrClient.ExtractAsync(docStream, mediaType, cancellationToken: cancellationToken);
            OcrCalls++;
            foreach (OcrPage page in result.Pages)
                document.Sections.Add(OcrPageToSection(page, result.OcrSource));
            return document;
        }

        // Native PdfPig text, with image-per-page OCR only for pages that need it.
        using var pdf = PdfDocument.Open(new MemoryStream(bytes, writable: false));
        IPageSegmenter segmenter = pageSegmenter ?? DefaultPageSegmenter.Instance;

        for (int i = 1; i <= pdf.NumberOfPages; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UglyToad.PdfPig.Content.Page page = pdf.GetPage(i);
            var section = new IngestionDocumentSection();
            section.Metadata["page_number"] = i;
            section.Metadata["ocr_source"] = "pdfpig-native";

            foreach (var block in segmenter.GetBlocks(page.GetWords()))
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                var para = new IngestionDocumentParagraph(block.Text) { Text = block.Text, PageNumber = i };
                para.Metadata["page_number"] = i;
                para.Metadata["ocr_source"] = "pdfpig-native";
                var b = block.BoundingBox;
                para.Metadata["BoundingBox.Left"] = b.Left;
                para.Metadata["BoundingBox.Bottom"] = b.Bottom;
                para.Metadata["BoundingBox.Right"] = b.Right;
                para.Metadata["BoundingBox.Top"] = b.Top;
                section.Elements.Add(para);
            }

            // Fallback OCR: the caller's predicate decides per page (default = OCR only empty pages).
            // The reader supplies the telemetry; the policy lives with the caller.
            if (policy == OcrPolicy.FallbackForEmptyPages && ocrClient is not null)
            {
                var ctx = new PageOcrContext(i, section.Elements.Count);
                bool shouldOcr = (ocrPagePredicate ?? (static c => !c.HasNativeText))(ctx);
                if (shouldOcr)
                {
                    // A scanned page: render it and OCR just this page. A born-digital PDF is fully
                    // digital, so this never fires. Rendering a page to an image needs a rasterizer
                    // (e.g. Docnet/PDFtoImage); wire one in for real scanned-PDF workloads. The seam —
                    // per-page fallback into the injected IOcrClient — is what #14 formalizes.
                    throw new NotSupportedException(
                        $"Page {i} has no digital text and would route to the injected IOcrClient; " +
                        "supply a page rasterizer to enable the scanned-page path.");
                }
            }

            document.Sections.Add(section);
        }

        return document;
    }

    private static IngestionDocumentSection OcrPageToSection(OcrPage page, string? ocrSource)
    {
        var section = new IngestionDocumentSection();
        section.Metadata["page_number"] = page.Index;
        section.Metadata["ocr_source"] = ocrSource ?? "ocr";
        if (!string.IsNullOrWhiteSpace(page.Markdown))
        {
            var para = new IngestionDocumentParagraph(page.Markdown) { Text = page.Markdown, PageNumber = page.Index };
            para.Metadata["page_number"] = page.Index;
            para.Metadata["ocr_source"] = ocrSource ?? "ocr";
            section.Elements.Add(para);
        }
        return section;
    }
}
