using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace DemoOcr;

// Choose native PdfPig extraction or whole-document extraction through the injected client.
public enum OcrPolicy
{
    Never,
    AllPages,
}

// Native text and whole-document extraction are both supported. Per-page fallback is intentionally
// absent because this demo has no PDF rasterizer; it must not claim to OCR empty pages and then throw.
public sealed class PdfPigReader(
    IDocumentExtractionClient? ocrClient = null,
    OcrPolicy policy = OcrPolicy.Never,
    IPageSegmenter? pageSegmenter = null) : IngestionDocumentReader
{
    public int OcrCalls { get; private set; }

    public override async Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        if (policy != OcrPolicy.Never && ocrClient is null)
            throw new ArgumentNullException(nameof(ocrClient), $"An {nameof(IDocumentExtractionClient)} is required when policy is {policy}.");

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await source.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        // Document-native archetype: the extraction result already owns the shared semantic tree.
        if (policy == OcrPolicy.AllPages && ocrClient is not null)
        {
            using var docStream = new MemoryStream(bytes, writable: false);
            DocumentExtractionResult result = await ocrClient.ExtractAsync(docStream, mediaType, cancellationToken: cancellationToken);
            OcrCalls++;
            var extracted = new IngestionDocument(identifier, result.Document);
            extracted.Metadata["reader"] = "document-extraction";
            extracted.Metadata["page_count"] = result.Pages.Count;
            return extracted;
        }

        // Native PdfPig text.
        using var pdf = PdfDocument.Open(new MemoryStream(bytes, writable: false));
        IPageSegmenter segmenter = pageSegmenter ?? DefaultPageSegmenter.Instance;
        var sections = new List<DocumentNode>();

        for (int i = 1; i <= pdf.NumberOfPages; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UglyToad.PdfPig.Content.Page page = pdf.GetPage(i);
            var blocks = new List<DocumentNode>();
            int blockIndex = 0;

            foreach (var block in segmenter.GetBlocks(page.GetWords()))
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                blocks.Add(DocumentExtractionDemoExtensions.CreateTextNode(
                    "pdfpig",
                    i,
                    blockIndex++,
                    block.Text));
            }

            sections.Add(new DocumentContainer(
                DocumentExtractionDemoExtensions.CreateNodeId("pdfpig", i, "section", 0),
                DocumentContainerRole.Section,
                blocks,
                pageReferences: [new(i)]));
        }

        var ingestion = new IngestionDocument(identifier, new Document(sections));
        ingestion.Metadata["reader"] = "pdfpig-native";
        ingestion.Metadata["page_count"] = pdf.NumberOfPages;
        return ingestion;
    }
}
