using System.Text.Json;
using System.Text.RegularExpressions;
using hero.Web.Services.Ingestion;
using Microsoft.Extensions.AI;

namespace hero.Web.Services;

public sealed class CitationRegistry
{
    private readonly Dictionary<string, CitationEvidence> _retrieved = new(StringComparer.Ordinal);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.NonBacktracking);

    public void Register(IEnumerable<IngestedChunk> records)
    {
        foreach (IngestedChunk record in records)
        {
            if (string.IsNullOrEmpty(record.CitationId))
            {
                throw new InvalidOperationException("The retrieved record has no citation identity. Reingest using the E2E collection.");
            }

            SourcePageEvidence[] pages = JsonSerializer.Deserialize<SourcePageEvidence[]>(record.SourcePagesJson)
                ?? throw new InvalidOperationException("The retrieved source-page evidence is null.");
            SourceRegionEvidence[] regions = JsonSerializer.Deserialize<SourceRegionEvidence[]>(record.SourceRegionsJson)
                ?? throw new InvalidOperationException("The retrieved region evidence is null.");
            string text = record.Content is TextContent content ? content.Text : "";
            _retrieved[record.CitationId] = new(record.Key, record.CitationId, record.DocumentId, text, pages, regions);
        }
    }

    public bool TryResolve(string id, string quote, out CitationEvidence? evidence)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(quote) || !_retrieved.TryGetValue(id, out CitationEvidence? candidate))
        {
            return false;
        }

        string normalizedQuote = Whitespace.Replace(quote, " ").Trim();
        if (!Whitespace.Replace(candidate.Text, " ").Contains(normalizedQuote, StringComparison.Ordinal))
        {
            return false;
        }

        evidence = candidate;
        return true;
    }

    public void Clear() => _retrieved.Clear();
}

public sealed record CitationEvidence(Guid RecordKey, string Id, string DocumentId, string Text,
    IReadOnlyList<SourcePageEvidence> Pages, IReadOnlyList<SourceRegionEvidence> Regions);
