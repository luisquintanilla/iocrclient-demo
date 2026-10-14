using System.Runtime.CompilerServices;

using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

public static class IngestionChunkExtensions
{
    public static IEnumerable<DocumentElement> EnumerateSourceElements(this IngestionChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var seen = new HashSet<DocumentElement>(ReferenceEqualityComparer.Instance);
        foreach (DocumentElement sourceElement in chunk.SourceElements)
        {
            for (DocumentElement? element = sourceElement;
                element is not null;
                element = element.ParentSection)
            {
                if (seen.Add(element))
                {
                    yield return element;
                }
            }
        }
    }

    public static IReadOnlyList<DocumentSourcePage> GetSourcePages(this IngestionChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var pages = new List<DocumentSourcePage>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var requestedReferences = new HashSet<string>(StringComparer.Ordinal);

        foreach (DocumentElement element in chunk.EnumerateSourceElements())
        {
            foreach (DocumentAnnotation annotation in element.Annotations)
            {
                foreach (DocumentRegion region in annotation.Regions)
                {
                    if (identifiers.Add(region.Page.Identifier))
                    {
                        pages.Add(region.Page);
                    }
                }
            }

            foreach (DocumentPageReference pageReference in element.PageReferences)
            {
                requestedReferences.Add(pageReference.Value);
            }
        }

        foreach (DocumentSourcePage page in chunk.Document.SourcePages)
        {
            if (requestedReferences.Contains(page.Identifier)
                && identifiers.Add(page.Identifier))
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<DocumentElement>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(DocumentElement? x, DocumentElement? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(DocumentElement obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}
