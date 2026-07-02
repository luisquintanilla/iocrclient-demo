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
/// ocr_source, confidence, element_type, BoundingBox.*). With #7516's opt-in
/// IngestionChunkerOptions.MetadataKeysToPropagate, those keys survive into chunks automatically; the
/// pipeline can then cite [page N] end to end.
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
            section.Metadata["page_number"] = page.Index;
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
                    PageNumber = page.Index,
                };
                paragraph.Metadata["page_number"] = page.Index;
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
                    PageNumber = page.Index,
                };
                blockPara.Metadata["page_number"] = page.Index;
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
                    PageNumber = page.Index,
                };
                tableEl.Metadata["page_number"] = page.Index;
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
        metadata["BoundingBox.Polygon"] = string.Join(",", r.Polygon);
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

    /// <summary>Axis-aligned bounds of the polygon, for the PdfPigReader BoundingBox.* keys.</summary>
    public static (float Left, float Top, float Right, float Bottom) GetBounds(this OcrBoundingRegion region)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i + 1 < region.Polygon.Count; i += 2)
        {
            minX = Math.Min(minX, region.Polygon[i]);
            maxX = Math.Max(maxX, region.Polygon[i]);
            minY = Math.Min(minY, region.Polygon[i + 1]);
            maxY = Math.Max(maxY, region.Polygon[i + 1]);
        }
        return (minX, minY, maxX, maxY);
    }
}
