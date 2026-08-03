using System.Text;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;

namespace DemoOcr;

/// <summary>
/// THE bridge — the line between IDocumentExtractionClient (a capability) and MEDI's IngestionDocumentReader (a
/// pipeline stage). ONE provider-agnostic reader that composes ANY <see cref="IDocumentExtractionClient"/>: Foundry
/// Mistral OCR, Azure Document Intelligence, Content Understanding, or a vision LLM — whatever you
/// inject. There is no per-engine reader and no VisionOnly flag; the engine is a constructor argument.
///
/// It maps the normalized <see cref="DocumentExtractionResult"/> onto an <see cref="IngestionDocument"/>, stamping
/// element PageNumber + Metadata with the SAME key conventions PdfPigReader uses (page_number,
/// ocr_source, confidence, element_type, BoundingBox.*). Because it emits one section per OCR page, a
/// consumer that chunks each page-section on its own tags every chunk with its source page — so the
/// pipeline can cite [page N] end to end on the shipping API (see samples 06/07).
/// </summary>
public sealed class OcrDocumentReader(IDocumentExtractionClient ocrClient, DocumentExtractionOptions? options = null) : IngestionDocumentReader
{
    public override async Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        DocumentExtractionResult result = await ocrClient
            .ExtractAsync(source, mediaType, options, cancellationToken)
            .ConfigureAwait(false);

        var document = new IngestionDocument(identifier);
        string ocrSource = result.GetModelId() ?? "ocr";

        foreach (DocumentPage page in result.Pages)
        {
            var section = new IngestionDocumentSection();
            section.Metadata["page_number"] = page.PageNumber;
            section.Metadata["ocr_source"] = ocrSource;
            if (page.AdditionalProperties?.TryGetValue("confidence", out object? pc) == true)
            {
                section.Metadata["confidence"] = pc;
            }

            if (!string.IsNullOrWhiteSpace(page.Text))
            {
                var paragraph = new IngestionDocumentParagraph(page.Text)
                {
                    Text = page.Text,
                    PageNumber = page.PageNumber,
                };
                paragraph.Metadata["page_number"] = page.PageNumber;
                paragraph.Metadata["ocr_source"] = ocrSource;
                if (page.AdditionalProperties?.TryGetValue("confidence", out object? c) == true)
                {
                    paragraph.Metadata["confidence"] = c;
                }
                section.Elements.Add(paragraph);
            }

            foreach (DocumentBlock block in page.Elements.OfType<DocumentBlock>())
            {
                if (string.IsNullOrWhiteSpace(block.Text))
                {
                    continue;
                }
                var blockPara = new IngestionDocumentParagraph(block.Text)
                {
                    Text = block.Text,
                    PageNumber = page.PageNumber,
                };
                blockPara.Metadata["page_number"] = page.PageNumber;
                blockPara.Metadata["ocr_source"] = ocrSource;
                if (block.Kind is { } kind)
                {
                    blockPara.Metadata["element_type"] = kind.Value;
                }
                if (block.Confidence is { } bc)
                {
                    blockPara.Metadata["confidence"] = bc;
                }
                AddRegion(blockPara.Metadata, block.BoundingRegion);
                section.Elements.Add(blockPara);
            }

            foreach (DocumentTable table in page.Elements.OfType<DocumentTable>())
            {
                string tableMarkdown = table.ToMarkdown();
                if (string.IsNullOrWhiteSpace(tableMarkdown))
                {
                    continue;
                }
                var tableEl = new IngestionDocumentParagraph(tableMarkdown)
                {
                    Text = tableMarkdown,
                    PageNumber = page.PageNumber,
                };
                tableEl.Metadata["page_number"] = page.PageNumber;
                tableEl.Metadata["ocr_source"] = ocrSource;
                tableEl.Metadata["element_type"] = "table";
                if (table.Cells is { Count: > 0 })
                {
                    tableEl.Metadata["table.rowCount"] = table.RowCount;
                    tableEl.Metadata["table.columnCount"] = table.ColumnCount;
                }
                AddRegion(tableEl.Metadata, table.BoundingRegion);
                section.Elements.Add(tableEl);
            }

            if (section.Elements.Count > 0)
            {
                document.Sections.Add(section);
            }
        }

        return document;
    }

    private static void AddRegion(IDictionary<string, object?> metadata, DocumentBoundingRegion? region)
    {
        if (region is not { } r)
        {
            return;
        }
        if (r.GetBounds() is { } bounds)
        {
            metadata["BoundingBox.Left"] = bounds.Left;
            metadata["BoundingBox.Top"] = bounds.Top;
            metadata["BoundingBox.Right"] = bounds.Right;
            metadata["BoundingBox.Bottom"] = bounds.Bottom;
        }
        metadata["BoundingBox.PageNumber"] = r.PageNumber;
        metadata["BoundingBox.Polygon"] = string.Join(",", r.Polygon.SelectMany(p => new[] { p.X, p.Y }));
    }
}

/// <summary>Small helpers over the real OCR types the bridge needs.</summary>
public static class OcrShapeExtensions
{
    /// <summary>
    /// Adapts a unary <see cref="IDocumentExtractionClient.ExtractAsync"/> into the streaming shape: yields one
    /// <see cref="DocumentExtractionPageResult"/> per completed page, then a terminal update carrying the
    /// document-level <c>ModelId</c>/<c>Usage</c>. Providers whose engine returns the whole document in
    /// one call reuse this so <see cref="IDocumentExtractionClient.ExtractPagesAsync"/> is a one-liner over the
    /// tested unary path; <see cref="DocumentExtractionPageResultExtensions.ToDocumentExtractionResultAsync"/> reassembles it.
    /// </summary>
    public static async IAsyncEnumerable<DocumentExtractionPageResult> StreamAsUpdates(
        Func<CancellationToken, Task<DocumentExtractionResult>> extract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DocumentExtractionResult result = await extract(cancellationToken).ConfigureAwait(false);
        int total = result.Pages.Count;
        int processed = 0;
        foreach (DocumentPage page in result.Pages)
        {
            yield return new DocumentExtractionPageResult(page)
            {
                PagesProcessed = ++processed,
                TotalPages = total,
                Usage = processed == total ? result.Usage : null,
                AdditionalProperties = processed == total ? result.AdditionalProperties : null,
            };
        }
    }

    public static string? GetModelId(this DocumentExtractionResult result)
        => result.AdditionalProperties?.TryGetValue("modelId", out object? modelId) == true
            ? modelId as string
            : null;

    public static bool GetIncludeImages(this DocumentExtractionOptions? options)
        => options?.AdditionalProperties?.TryGetValue("includeImages", out object? includeImages) == true
            && includeImages is true;

    /// <summary>The engine's markdown if present, else a GitHub table built from cells.</summary>
    public static string ToMarkdown(this DocumentTable table)
    {
        if (!string.IsNullOrWhiteSpace(table.MarkdownRepresentation))
        {
            return table.MarkdownRepresentation!;
        }
        if (table.Cells is not { Count: > 0 } cells)
        {
            return string.Empty;
        }

        var grid = new string[table.RowCount, table.ColumnCount];
        foreach (DocumentTableCell cell in cells)
        {
            if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
            {
                grid[cell.RowIndex, cell.ColumnIndex] = cell.Content.Replace("|", "\\|").Replace("\n", " ");
            }
        }

        var sb = new StringBuilder();
        for (int r = 0; r < table.RowCount; r++)
        {
            sb.Append("| ");
            for (int c = 0; c < table.ColumnCount; c++)
            {
                sb.Append(grid[r, c] ?? "").Append(" | ");
            }
            sb.AppendLine();
            if (r == 0)
            {
                sb.Append("| ");
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    sb.Append("--- | ");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
