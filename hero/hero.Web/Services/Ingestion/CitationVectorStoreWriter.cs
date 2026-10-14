using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DemoOcr;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.VectorData;

namespace hero.Web.Services.Ingestion;

public sealed class CitationVectorStoreWriter(
    VectorStoreCollection<Guid, IngestedChunk> collection,
    VectorStoreWriterOptions? options = null) : VectorStoreWriter<IngestedChunk>(collection, options)
{
    protected override IngestedChunk CreateRecord(IngestionChunk chunk)
    {
        IngestedChunk record = base.CreateRecord(chunk);
        if (chunk.Document.Metadata.TryGetValue("demo.source.sha256", out object? sourceHash) && sourceHash is string hash)
        {
            record.SourceHash = hash;
        }
        var pages = new Dictionary<string, SourcePageEvidence>(StringComparer.Ordinal);
        var regions = new List<SourceRegionEvidence>();
        foreach (DocumentSourcePage page in chunk.GetSourcePages())
        {
            AddPage(page);
        }
        foreach (DocumentElement element in chunk.EnumerateSourceElements())
        {
            foreach (DocumentAnnotation annotation in element.Annotations)
            {
                foreach (DocumentRegion region in annotation.Regions)
                {
                    regions.Add(new(region.Page.Identifier, annotation.Kind, annotation.Role,
                        region.Polygon.Select(p => new[] { p.X, p.Y }).ToArray(),
                        annotation.Confidence?.Kind, annotation.Confidence?.Value));
                }
            }
        }

        SourcePageEvidence[] orderedPages = pages.Values.OrderBy(p => p.PageNumber).ThenBy(p => p.Identifier, StringComparer.Ordinal).ToArray();
        record.SourcePagesJson = JsonSerializer.Serialize(orderedPages);
        record.SourceRegionsJson = JsonSerializer.Serialize(regions);
        string identity = JsonSerializer.Serialize(new
        {
            record.DocumentId,
            record.SourceHash,
            Pages = orderedPages.Select(p => p.PageNumber).Distinct().ToArray(),
            record.SerializedContent,
        });
        record.CitationId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return record;

        void AddPage(DocumentSourcePage page)
            => pages.TryAdd(page.Identifier, new(page.Identifier, page.PageNumber,
                page.Dimensions?.Width, page.Dimensions?.Height,
                page.CoordinateUnit?.ToString(), page.CoordinateOrigin?.ToString()));
    }
}

public sealed record SourcePageEvidence(string Identifier, int PageNumber, double? Width, double? Height, string? Unit, string? Origin);

public sealed record SourceRegionEvidence(string PageIdentifier, string Kind, string? Role, double[][] Polygon, string? ConfidenceKind, double? Confidence);
