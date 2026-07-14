using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;

namespace DemoOcr;

/// <summary>
/// THE bridge — the line between IOcrClient (a capability) and MEDI's IngestionDocumentReader (a
/// pipeline stage). ONE provider-agnostic reader that composes ANY <see cref="IOcrClient"/>: Foundry
/// Mistral OCR, Azure Document Intelligence, Content Understanding, or a vision LLM — whatever you
/// inject. There is no per-engine reader and no VisionOnly flag; the engine is a constructor argument.
///
/// It maps the normalized <see cref="OcrResult"/> onto an <see cref="IngestionDocument"/>, stamping
/// element PageNumber + Metadata with the SAME key conventions PdfPigReader uses (page_number,
/// ocr_source, confidence, element_type, BoundingBox.*). Because it emits one section per OCR page, a
/// consumer that chunks each page-section on its own tags every chunk with its source page — so the
/// pipeline can cite [page N] end to end on the shipping API (see samples 06/07).
/// </summary>
public sealed class OcrDocumentReader(IOcrClient ocrClient, OcrOptions? options = null) : IngestionDocumentReader
{
    public override async Task<IngestionDocument> ReadAsync(
        Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        OcrResult result = await ocrClient
            .ExtractAsync(source, mediaType, options, progress: null, cancellationToken)
            .ConfigureAwait(false);

        var document = new IngestionDocument(identifier);
        string ocrSource = result.OcrSource ?? "ocr";

        foreach (OcrPage page in result.Pages)
        {
            var section = new IngestionDocumentSection();
            section.Metadata["page_number"] = page.PageNumber;
            section.Metadata["ocr_source"] = ocrSource;
            if (page.Confidence is { } pc)
            {
                section.Metadata["confidence"] = pc;
            }

            if (!string.IsNullOrWhiteSpace(page.Markdown))
            {
                var paragraph = new IngestionDocumentParagraph(page.Markdown)
                {
                    Text = page.Markdown,
                    PageNumber = page.PageNumber,
                };
                paragraph.Metadata["page_number"] = page.PageNumber;
                paragraph.Metadata["ocr_source"] = ocrSource;
                if (page.Confidence is { } c)
                {
                    paragraph.Metadata["confidence"] = c;
                }
                section.Elements.Add(paragraph);
            }

            foreach (OcrBlock block in page.Blocks)
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
                    blockPara.Metadata["element_type"] = kind;
                }
                if (block.Confidence is { } bc)
                {
                    blockPara.Metadata["confidence"] = bc;
                }
                AddRegion(blockPara.Metadata, block.BoundingRegion);
                section.Elements.Add(blockPara);
            }

            foreach (OcrTable table in page.Tables)
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

    private static void AddRegion(IDictionary<string, object?> metadata, OcrBoundingRegion? region)
    {
        if (region is not { } r)
        {
            return;
        }
        (float left, float top, float right, float bottom) = r.GetBounds();
        metadata["BoundingBox.Left"] = left;
        metadata["BoundingBox.Top"] = top;
        metadata["BoundingBox.Right"] = right;
        metadata["BoundingBox.Bottom"] = bottom;
        metadata["BoundingBox.PageNumber"] = r.PageNumber;
        metadata["BoundingBox.Polygon"] = string.Join(",", r.Polygon.SelectMany(p => new[] { p.X, p.Y }));
    }
}

/// <summary>Small helpers over the real (sealed) OCR types the bridge needs.</summary>
public static class OcrShapeExtensions
{
    /// <summary>The engine's markdown if present, else a GitHub table built from cells.</summary>
    public static string ToMarkdown(this OcrTable table)
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
        foreach (OcrTableCell cell in cells)
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
