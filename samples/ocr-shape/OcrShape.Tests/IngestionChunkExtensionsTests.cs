using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr.Tests;

public sealed class IngestionChunkExtensionsTests
{
    [Fact]
    public void EnumerateSourceElements_IncludesAncestorsOnceByReference()
    {
        using var document = new Document("doc", "stored");
        var root = new DocumentSection("# root");
        var nested = new DocumentSection("## nested");
        var child = new DocumentTextElement(DocumentTextKind.Paragraph, "child", "child");
        document.Sections.Add(root);
        root.Elements.Add(nested);
        nested.Elements.Add(child);
        IngestionChunk chunk = CreateChunk(document);
        SetSourceElements(chunk, child, nested, child);

        DocumentElement[] elements = chunk.EnumerateSourceElements().ToArray();

        Assert.Equal(3, elements.Length);
        Assert.Same(child, elements[0]);
        Assert.Same(nested, elements[1]);
        Assert.Same(root, elements[2]);
        Assert.Equal(3, elements.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void GetSourcePages_PrefersRegionPagesAndDeduplicatesThem()
    {
        using var document = new Document("doc", "stored");
        DocumentSourcePage regionPage = Page("region-page", 9);
        DocumentSourcePage referencedPage = Page("reference-page", 2);
        document.AddSourcePage(referencedPage);
        var element = new DocumentTextElement(DocumentTextKind.Paragraph, "text", "text");
        element.Annotations.Add(new DocumentAnnotation(
            "grounding",
            regions:
            [
                new DocumentRegion(
                    regionPage,
                    [
                        new DocumentPoint(1, 1),
                        new DocumentPoint(2, 1),
                        new DocumentPoint(2, 2),
                    ]),
                new DocumentRegion(
                    regionPage,
                    [
                        new DocumentPoint(3, 3),
                        new DocumentPoint(4, 3),
                        new DocumentPoint(4, 4),
                    ]),
            ]));
        element.PageReferences.Add(new DocumentPageReference("reference-page"));
        IngestionChunk chunk = CreateChunk(document);
        SetSourceElements(chunk, element);

        IReadOnlyList<DocumentSourcePage> pages = chunk.GetSourcePages();

        Assert.Equal(2, pages.Count);
        Assert.Same(regionPage, pages[0]);
        Assert.Same(referencedPage, pages[1]);
        Assert.Equal(["region-page", "reference-page"], pages.Select(page => page.Identifier));
    }

    [Fact]
    public void GetSourcePages_ResolvesOnlyExactPageIdentifiers()
    {
        using var document = new Document("doc", "stored");
        DocumentSourcePage exact = Page("provider:page:02", 2);
        DocumentSourcePage near = Page("provider:page:2", 22);
        document.AddSourcePage(exact);
        document.AddSourcePage(near);
        var element = new DocumentTextElement(DocumentTextKind.Paragraph, "text", "text");
        element.PageReferences.Add(new DocumentPageReference("provider:page:02"));
        IngestionChunk chunk = CreateChunk(document);
        SetSourceElements(chunk, element);

        DocumentSourcePage page = Assert.Single(chunk.GetSourcePages());

        Assert.Same(exact, page);
        Assert.Equal(2, page.PageNumber);
        Assert.NotSame(near, page);
    }

    [Fact]
    public void GetSourcePages_DoesNotParseIntegerReferencesOrFabricatePageOne()
    {
        using var document = new Document("doc", "stored");
        DocumentSourcePage pageOne = Page("opaque-provider-page-one", 1);
        document.AddSourcePage(pageOne);
        var element = new DocumentTextElement(DocumentTextKind.Paragraph, "text", "text");
        element.PageReferences.Add(new DocumentPageReference("1"));
        IngestionChunk integerChunk = CreateChunk(document);
        SetSourceElements(integerChunk, element);
        IngestionChunk unassociatedChunk = CreateChunk(document);

        IReadOnlyList<DocumentSourcePage> integerPages = integerChunk.GetSourcePages();
        IReadOnlyList<DocumentSourcePage> unassociatedPages = unassociatedChunk.GetSourcePages();

        Assert.Empty(integerPages);
        Assert.Empty(unassociatedPages);
        Assert.Single(document.SourcePages);
        Assert.Equal("opaque-provider-page-one", document.SourcePages[0].Identifier);
    }

    private static IngestionChunk CreateChunk(Document document)
        => new(new TextContent("chunk"), document, tokenCount: 1, context: "test");

    private static void SetSourceElements(
        IngestionChunk chunk,
        params DocumentElement[] elements)
    {
        var property = typeof(IngestionChunk).GetProperty(nameof(IngestionChunk.SourceElements))!;
        if (property.GetSetMethod(nonPublic: true) is { } setter)
        {
            setter.Invoke(chunk, [elements]);
            return;
        }

        var field = typeof(IngestionChunk).GetField(
            $"<{nameof(IngestionChunk.SourceElements)}>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(chunk, elements);
    }

    private static DocumentSourcePage Page(string identifier, int pageNumber)
    {
        DocumentSourceOutput output = ProviderAdapterHelpers.CreateSourceOutput(
            identifier,
            "fake",
            "model",
            new DocumentExtractionOptions(),
            nativeRepresentation: null);
        return new DocumentSourcePage(identifier, pageNumber, sourceOutput: output);
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<DocumentElement>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(DocumentElement? x, DocumentElement? y) => ReferenceEquals(x, y);

        public int GetHashCode(DocumentElement obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
