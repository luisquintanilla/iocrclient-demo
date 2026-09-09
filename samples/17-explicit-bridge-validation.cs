#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package CommunityToolkit.VectorData.InMemory@1.0.0-preview.3
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003, IL2026, IL3002, IL3050

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using CommunityToolkit.VectorData.InMemory;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

const string ExtensionsSha = "c1913907f05148370a84824b669d73249bb502e4";
const string PackageVersion = "10.8.0-preview2bridge.c191390";
VerifyFeedAndLoadedAssemblies(ExtensionsSha, PackageVersion);

DocumentExtractionResult extraction = FixtureExtractionClient.CreateScenario();
using var client = new FixtureExtractionClient(extraction);
var reader = new DocumentExtractionReader(client);
var chunker = new CapturingChunker(new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
{
    MaxTokensPerChunk = 256,
    OverlapTokens = 0,
}));
var processor = new RecordingProcessor();
using var embeddings = new RecordingEmbeddingGenerator();
using var store = new InMemoryVectorStore(new() { EmbeddingGenerator = embeddings });
VectorStoreCollection<Guid, Preview2ChunkRecord> collection =
    store.GetIngestionRecordCollection<Preview2ChunkRecord>(
        "preview2-bridge",
        RecordingEmbeddingGenerator.DimensionCount);
using var writer = new VectorStoreWriter<Preview2ChunkRecord>(collection);
using var pipeline = new IngestionPipeline(reader, chunker, writer);
pipeline.ChunkProcessors.Add(processor);

string fixturePath = Path.Combine(Path.GetTempPath(), "preview2-explicit-bridge-fixture.pdf");
await File.WriteAllBytesAsync(fixturePath, [1, 2, 3]);
IngestionResult pipelineResult;
try
{
    pipelineResult = await SingleAsync(pipeline.ProcessAsync([new FileInfo(fixturePath)]));
}
finally
{
    File.Delete(fixturePath);
}

Check(pipelineResult.Succeeded && pipelineResult.Document is not null,
    "The non-generic Preview 2 pipeline did not succeed.");
IngestionDocument ingestion = pipelineResult.Document!;
Check(client.ExtractCallCount == 1
        && client.LastMediaType == "application/pdf"
        && client.LastOptions is null,
    "The built-in reader did not invoke the fake public client exactly once.");
Check(ReferenceEquals(client.SourceResult, extraction),
    "The fake client did not return the authored extraction result.");

List<IngestionDocumentElement> mapped = ingestion.EnumerateContent().ToList();
Type[] mappedTypes =
[
    typeof(IngestionDocumentHeader),
    typeof(IngestionDocumentParagraph),
    typeof(IngestionDocumentTable),
    typeof(IngestionDocumentImage),
    typeof(IngestionDocumentParagraph),
    typeof(IngestionDocumentHeader),
    typeof(IngestionDocumentParagraph),
];
Check(ingestion.Sections.Select(section => section.PageNumber).SequenceEqual([1, 2])
        && ingestion.Sections.All(section => !section.HasMetadata)
        && mapped.Select(element => element.GetType()).SequenceEqual(mappedTypes)
        && mapped.Take(5).All(element => element.PageNumber == 1)
        && mapped.Skip(5).All(element => element.PageNumber == 2),
    "The exact mapped page/type projection changed.");

IngestionDocumentHeader heading = (IngestionDocumentHeader)mapped[0];
IngestionDocumentParagraph summary = (IngestionDocumentParagraph)mapped[1];
IngestionDocumentTable table = (IngestionDocumentTable)mapped[2];
IngestionDocumentImage image = (IngestionDocumentImage)mapped[3];
IngestionDocumentParagraph unknownKind = (IngestionDocumentParagraph)mapped[4];
IngestionDocumentHeader appendix = (IngestionDocumentHeader)mapped[5];
IngestionDocumentParagraph retention = (IngestionDocumentParagraph)mapped[6];
Check(heading.Text == "Quarterly Review" && heading.IsLiteralText
        && summary.Text == "Revenue increased after the bridge rollout." && summary.IsLiteralText
        && unknownKind.Text == "provider-specific note"
        && appendix.Text == "Appendix"
        && retention.Text == "Retention policy remains unchanged.",
    "Heading, paragraph, unknown-kind, or retention mapping changed.");

IReadOnlyList<IngestionDocumentTableCell> cells = table.StructuredCells
    ?? throw new InvalidOperationException("The mapped table lost structured cells.");
Check(cells.Count == 4
        && table.Cells.GetLength(0) == 2
        && table.Cells.GetLength(1) == 2
        && Cell(cells, 0, 0, DocumentTableCellKind.ColumnHeader.Value).Text == "Metric"
        && Cell(cells, 0, 1, DocumentTableCellKind.ColumnHeader.Value).Text == "Value"
        && Cell(cells, 1, 0, DocumentTableCellKind.RowHeader.Value).Text == "Revenue"
        && Cell(cells, 1, 1, DocumentTableCellKind.Content.Value).Text == "$12M",
    "The exact 2x2 table shape, roles, or values changed.");
Check(image.Content is { } imageBytes
        && imageBytes.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 })
        && image.MediaType == "image/png"
        && image.AlternativeText == "Quarterly revenue chart",
    "The mapped image changed bytes, media type, or caption.");

AssertExtractionEvidenceAndLoss(extraction, ingestion, mapped, cells);
await CheckMarkdownBoundaryAsync();
await CheckPdfPigMetadataContractAsync(extraction);

List<IngestionChunk> chunks = chunker.Chunks;
string expectedPage1 = string.Join(
    Environment.NewLine,
    [
        "Quarterly Review",
        "Revenue increased after the bridge rollout.",
        "Table 2x2",
        "r1c1 rowSpan=1 columnSpan=1 kind=columnHeader: Metric",
        "r1c2 rowSpan=1 columnSpan=1 kind=columnHeader: Value",
        "r2c1 rowSpan=1 columnSpan=1 kind=rowHeader: Revenue",
        "r2c2 rowSpan=1 columnSpan=1 kind=content: $12M",
        "Quarterly revenue chart",
        "provider-specific note",
    ]);
string expectedPage2 = string.Join(
    Environment.NewLine,
    ["Appendix", "Retention policy remains unchanged."]);
Check(chunks.Count == 2
        && processor.Chunks.SequenceEqual(chunks)
        && chunks.All(chunk => !chunk.HasMetadata)
        && chunks.All(chunk => chunk.Content is TextContent && chunk.TokenCount > 0)
        && chunks[0].Content is TextContent page1 && page1.Text == expectedPage1
        && chunks[1].Content is TextContent page2 && page2.Text == expectedPage2
        && chunks[0].Context == "Quarterly Review"
        && chunks[1].Context == "Appendix"
        && chunks[0].PageNumbers.SequenceEqual([1])
        && chunks[1].PageNumbers.SequenceEqual([2]),
    "Non-generic chunks changed content, TokenCount, context, processor flow, or pages.");

List<Preview2ChunkRecord> records = await collection
    .GetAsync(record => record.DocumentId == ingestion.Identifier, top: 10)
    .ToListAsync();
Check(records.Count == chunks.Count
        && Record(records, 1).Content is TextContent stored1 && stored1.Text == expectedPage1
        && Record(records, 1).Context == "Quarterly Review"
        && Record(records, 2).Content is TextContent stored2 && stored2.Text == expectedPage2
        && Record(records, 2).Context == "Appendix"
        && records.All(record => record.SerializedContent is not null
            && record.SerializedPageNumbers is "1" or "2"
            && !record.SerializedContent.Contains(
                FixtureExtractionClient.EvidenceMarker,
                StringComparison.Ordinal)
            && !record.SerializedContent.Contains(
                FixtureExtractionClient.ProviderMarkdown,
                StringComparison.Ordinal)),
    "Typed stock writer did not persist exact polymorphic content, context, or pages.");
Check(embeddings.InputTypes.Count == 2
        && embeddings.InputTypes.All(type => type == typeof(TextContent)),
    "Configured VectorData provider did not embed both text records during upsert.");

Preview2ChunkRecord revenue = await FirstAsync(
    collection.SearchAsync(new TextContent("quarterly revenue"), top: 1));
Preview2ChunkRecord retained = await FirstAsync(
    collection.SearchAsync(new TextContent("retention unchanged"), top: 1));
Check(revenue.PageNumbers.SequenceEqual([1])
        && revenue.Content is TextContent revenueText && revenueText.Text == expectedPage1
        && retained.PageNumbers.SequenceEqual([2])
        && retained.Content is TextContent retentionText && retentionText.Text == expectedPage2,
    "Vector retrieval did not return exact page-specific content.");

MixedResult mixed = await CheckMixedContentAsync();

Console.WriteLine($"Source: {ExtensionsSha}");
Console.WriteLine("Feed: 6/6 hashes+id+version+repo+commit");
Console.WriteLine($"Client/pipeline: calls={client.ExtractCallCount} processors=1 processed_chunks={processor.Chunks.Count}");
Console.WriteLine($"Mapped: pages=1,2 types={string.Join(',', mapped.Select(element => element.GetType().Name))}");
Console.WriteLine($"Chunks: count=2 types=TextContent|TextContent tokens={chunks[0].TokenCount}|{chunks[1].TokenCount} pages=1|2");
Console.WriteLine("Writer: typed=Preview2ChunkRecord stored=2 pages=1|2 embeddings=TextContent,TextContent");
Console.WriteLine("Retrieval: revenue=page1:$12M retention=page2:unchanged");
Console.WriteLine($"Mixed: chunks=TextContent({mixed.TextTokens})|DataContent(0) stored=2 roundtrip=true embeddings=TextContent,DataContent");
Console.WriteLine("Markdown: source Text=empty default=error(page4) preserve=exact");
Console.WriteLine("PdfPig: pages=1|2 metadata=page_number,ocr_source");
Console.WriteLine("Loss: extraction evidence retained; ingestion/chunks/records isolated");
Console.WriteLine("PASS: Preview2 non-generic bridge -> chunker -> processor -> typed writer -> provider embedding -> retrieval");

static IngestionDocumentElement Cell(
    IReadOnlyList<IngestionDocumentTableCell> cells,
    int row,
    int column,
    string expectedKind)
{
    IngestionDocumentTableCell cell = cells.Single(
        candidate => candidate.RowIndex == row && candidate.ColumnIndex == column);
    Check(cell.Kind == expectedKind, $"Cell ({row},{column}) role changed.");
    return cell.Elements.Single();
}

static void AssertExtractionEvidenceAndLoss(
    DocumentExtractionResult extraction,
    IngestionDocument ingestion,
    IReadOnlyList<IngestionDocumentElement> mapped,
    IReadOnlyList<IngestionDocumentTableCell> cells)
{
    DocumentBlock title = (DocumentBlock)extraction.Pages[0].Elements[0];
    DocumentTable sourceTable = extraction.Pages[0].Elements.OfType<DocumentTable>().Single();
    DocumentTableCell sourceCell = sourceTable.Cells!.Single(
        cell => cell.RowIndex == 0 && cell.ColumnIndex == 0);
    DocumentImage sourceImage = extraction.Pages[0].Elements.OfType<DocumentImage>().Single();
    Check(extraction.RawRepresentation is not null
            && extraction.AdditionalProperties?[FixtureExtractionClient.ProviderResultKey]
                as string == FixtureExtractionClient.EvidenceMarker
            && extraction.Pages[0].RawRepresentation is not null
            && extraction.Pages[0].Dimensions is not null
            && extraction.Pages[0].CoordinateUnit == DocumentCoordinateUnit.Inch
            && extraction.Pages[0].AdditionalProperties?[FixtureExtractionClient.ProviderPageKey]
                as string == FixtureExtractionClient.EvidenceMarker
            && title.Confidence == 0.99
            && title.BoundingRegion is not null
            && title.RawRepresentation is not null
            && title.AdditionalProperties is { Count: > 0 }
            && sourceTable.RawRepresentation is not null
            && sourceTable.AdditionalProperties is { Count: > 0 }
            && sourceCell.Confidence == 0.95
            && sourceCell.RawRepresentation is not null
            && sourceImage.RawRepresentation is not null,
        "Authored extraction-only evidence was not retained at the source.");
    Check(ingestion.Sections.All(section => !section.HasMetadata)
            && mapped.All(element => !element.HasMetadata)
            && cells.SelectMany(cell => cell.Elements).All(element => !element.HasMetadata)
            && !string.Join("\n", mapped.Select(element => element.GetMarkdown()))
                .Contains(FixtureExtractionClient.EvidenceMarker, StringComparison.Ordinal)
            && !string.Join("\n", mapped.Select(element => element.GetMarkdown()))
                .Contains(FixtureExtractionClient.ProviderMarkdown, StringComparison.Ordinal),
        "Extraction evidence or duplicate Markdown leaked into ingestion.");
}

static async Task CheckMarkdownBoundaryAsync()
{
    const string Markdown = "## Supplement\n\nExact **provider Markdown**.";
    DocumentExtractionResult markdownOnly = new([new DocumentPage(4, [], Markdown)]);
    Check(markdownOnly.Pages.Single().Markdown == Markdown
            && markdownOnly.Pages.Single().Text == string.Empty
            && markdownOnly.Text == string.Empty,
        "Markdown-only source unexpectedly fell back into Text.");

    using (var defaultClient = new FixtureExtractionClient(markdownOnly))
    {
        try
        {
            await new DocumentExtractionReader(defaultClient).ReadAsync(
                new MemoryStream([4]), "markdown-only.pdf", "application/pdf");
            throw new InvalidOperationException("Default Markdown policy unexpectedly succeeded.");
        }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains("page 4", StringComparison.Ordinal)
                    && error.Message.Contains(nameof(MarkdownOnlyPagePolicy.PreserveAsMarkdown), StringComparison.Ordinal),
                "Default Markdown failure lacked context and opt-in guidance.");
        }
    }

    using var preserveClient = new FixtureExtractionClient(markdownOnly);
    IngestionDocument preserved = await new DocumentExtractionReader(
        preserveClient,
        new() { MarkdownOnlyPagePolicy = MarkdownOnlyPagePolicy.PreserveAsMarkdown })
        .ReadAsync(new MemoryStream([4]), "markdown-only.pdf", "application/pdf");
    IngestionDocumentParagraph paragraph = (IngestionDocumentParagraph)
        preserved.Sections.Single().Elements.Single();
    Check(paragraph.Text is null
            && paragraph.GetMarkdown() == Markdown
            && paragraph.PageNumber == 4,
        "Explicit Markdown policy did not preserve exact authored Markdown.");
}

static async Task CheckPdfPigMetadataContractAsync(DocumentExtractionResult extraction)
{
    using var client = new FixtureExtractionClient(extraction);
    var reader = new PdfPigReader(client, OcrPolicy.AllPages);
    IngestionDocument document = await reader.ReadAsync(
        new MemoryStream([1]), "quarterly-review.pdf", "application/pdf");
    Check(reader.OcrCalls == 1
            && document.Sections.Select(section => section.PageNumber).SequenceEqual([1, 2])
            && document.Sections.All(section =>
                (int)section.Metadata["page_number"]! == section.PageNumber
                && (string)section.Metadata["ocr_source"]! == FixtureExtractionClient.ModelId)
            && document.EnumerateContent().All(element =>
                (int)element.Metadata["page_number"]! == element.PageNumber
                && (string)element.Metadata["ocr_source"]! == FixtureExtractionClient.ModelId),
        "PdfPig AllPages metadata contract changed.");
}

static async Task<MixedResult> CheckMixedContentAsync()
{
    DocumentExtractionResult extraction = new(
    [
        new DocumentPage(
            1,
            [
                new DocumentBlock("Binary appendix"),
                new DocumentImage
                {
                    Content = new byte[] { 9, 8, 7 },
                    MediaType = "image/png",
                },
            ]),
    ]);
    using var client = new FixtureExtractionClient(extraction);
    IngestionDocument document = await new DocumentExtractionReader(client).ReadAsync(
        new MemoryStream([1]), "mixed.pdf", "application/pdf");
    var chunker = new SectionChunker(new(TiktokenTokenizer.CreateForModel("gpt-4o"))
    {
        MaxTokensPerChunk = 256,
        OverlapTokens = 0,
    });
    List<IngestionChunk> chunks = await chunker.ProcessAsync(document).ToListAsync();
    IngestionChunk text = chunks.Single(chunk => chunk.Content is TextContent);
    IngestionChunk binary = chunks.Single(chunk => chunk.Content is DataContent);
    Check(text.TokenCount > 0
            && binary.TokenCount == 0
            && text.PageNumbers.SequenceEqual([1])
            && binary.PageNumbers.SequenceEqual([1])
            && binary.Content is DataContent data
            && data.MediaType == "image/png"
            && data.Data.Span.SequenceEqual(new byte[] { 9, 8, 7 }),
        "Built-in chunker did not emit honest TextContent/DataContent token/page shapes.");

    using var embeddings = new RecordingEmbeddingGenerator();
    using var store = new InMemoryVectorStore(new() { EmbeddingGenerator = embeddings });
    VectorStoreCollection<Guid, Preview2ChunkRecord> collection =
        store.GetIngestionRecordCollection<Preview2ChunkRecord>(
            "mixed",
            RecordingEmbeddingGenerator.DimensionCount);
    using var writer = new VectorStoreWriter<Preview2ChunkRecord>(collection);
    await writer.WriteAsync(chunks.ToAsyncEnumerable());
    List<Preview2ChunkRecord> records = await collection.GetAsync(
        record => record.DocumentId == document.Identifier,
        top: 10).ToListAsync();
    Check(records.Count == 2
            && records.Any(record => record.Content is TextContent)
            && records.Any(record => record.Content is DataContent)
            && embeddings.InputTypes.SequenceEqual([typeof(TextContent), typeof(DataContent)]),
        "Typed writer/provider path did not persist and embed both AIContent types.");

    foreach (Preview2ChunkRecord record in records)
    {
        Preview2ChunkRecord roundTrip = new()
        {
            SerializedContent = record.SerializedContent,
            SerializedPageNumbers = record.SerializedPageNumbers,
        };
        bool sameContent = (record.Content, roundTrip.Content) switch
        {
            (TextContent before, TextContent after) => before.Text == after.Text,
            (DataContent before, DataContent after) =>
                before.MediaType == after.MediaType && before.Data.Span.SequenceEqual(after.Data.Span),
            _ => false,
        };
        Check(sameContent && roundTrip.PageNumbers.SequenceEqual(record.PageNumbers),
            "Polymorphic AIContent/page serialization round trip changed.");
    }
    return new(text.TokenCount);
}

static void VerifyFeedAndLoadedAssemblies(string extensionsSha, string packageVersion)
{
    string current = Directory.GetCurrentDirectory();
    string feed = Directory.Exists(Path.Combine(current, "local-feed"))
        ? Path.Combine(current, "local-feed")
        : Path.GetFullPath(Path.Combine(current, "..", "local-feed"));
    Check(Directory.Exists(feed), $"Could not locate local-feed from '{current}'.");

    (string Id, string Hash, Assembly Assembly)[] packages =
    [
        ("Microsoft.Extensions.AI", "a8192d63fa45ad84cfb018107c8431290e1aee6f7cd8454c1fac4302c3f085ad", typeof(ChatClientBuilder).Assembly),
        ("Microsoft.Extensions.AI.Abstractions", "13ec6febf70c77f7352e736b6e54e469706be435895271fb05fa0a91b6e3fecb", typeof(AIContent).Assembly),
        ("Microsoft.Extensions.DataIngestion", "569c315c3f8fc5d80140db53fb5f13046d6535967d61f4061f6029cbc73caa81", typeof(SectionChunker).Assembly),
        ("Microsoft.Extensions.DataIngestion.Abstractions", "06a201a6687b5abfb3e593f1557614e2071cba7cecdb2d3d5d2383459d61acff", typeof(IngestionChunk).Assembly),
        ("Microsoft.Extensions.DataIngestion.DocumentExtraction", "904f50db70912c45230e55c52446e3dc776d3a4eb79eaff11345c859d516f95a", typeof(DocumentExtractionReader).Assembly),
        ("Microsoft.Extensions.DocumentExtraction.Abstractions", "79dc4282564a82a2a21c6347d9a964d2e05be49308d11644e27a47152ae58c2c", typeof(DocumentPage).Assembly),
    ];
    Check(Directory.GetFiles(feed, "*.nupkg").Length == packages.Length,
        "Local feed contains missing or extra packages.");

    foreach ((string id, string hash, Assembly assembly) in packages)
    {
        string path = Path.Combine(feed, $"{id}.{packageVersion}.nupkg");
        Check(File.Exists(path)
                && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                    .Equals(hash, StringComparison.OrdinalIgnoreCase),
            $"Package hash mismatch: {id}.");
        using ZipArchive zip = ZipFile.OpenRead(path);
        ZipArchiveEntry nuspecEntry = zip.Entries.Single(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        XDocument nuspec = XDocument.Load(nuspecEntry.Open());
        XNamespace ns = nuspec.Root!.Name.Namespace;
        XElement metadata = nuspec.Root.Element(ns + "metadata")!;
        XElement repository = metadata.Element(ns + "repository")!;
        Check(metadata.Element(ns + "id")?.Value == id
                && metadata.Element(ns + "version")?.Value == packageVersion
                && repository.Attribute("url")?.Value == "https://github.com/luisquintanilla/extensions.git"
                && repository.Attribute("commit")?.Value == extensionsSha,
            $"Package provenance mismatch: {id}.");
        ZipArchiveEntry dll = zip.GetEntry($"lib/net10.0/{id}.dll")
            ?? throw new InvalidOperationException($"Missing net10.0 assembly: {id}.");
        using var packageBytes = new MemoryStream();
        dll.Open().CopyTo(packageBytes);
        Check(SHA256.HashData(File.ReadAllBytes(assembly.ManifestModule.FullyQualifiedName))
                .SequenceEqual(SHA256.HashData(packageBytes.ToArray())),
            $"Loaded assembly does not match package: {id}.");
    }
}

static Preview2ChunkRecord Record(IEnumerable<Preview2ChunkRecord> records, int page)
    => records.Single(record => record.PageNumbers.SequenceEqual([page]));

static async Task<T> SingleAsync<T>(IAsyncEnumerable<T> values)
{
    List<T> result = await values.ToListAsync();
    return result.Single();
}

static async Task<T> FirstAsync<T>(IAsyncEnumerable<VectorSearchResult<T>> values)
    where T : class
{
    await foreach (VectorSearchResult<T> value in values)
    {
        return value.Record;
    }
    throw new InvalidOperationException("Expected a vector result.");
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class CapturingChunker(IngestionChunker inner) : IngestionChunker
{
    public List<IngestionChunk> Chunks { get; } = [];

    public override async IAsyncEnumerable<IngestionChunk> ProcessAsync(
        IngestionDocument document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (IngestionChunk chunk in inner.ProcessAsync(document, cancellationToken))
        {
            Chunks.Add(chunk);
            yield return chunk;
        }
    }
}

sealed class RecordingProcessor : IngestionChunkProcessor
{
    public List<IngestionChunk> Chunks { get; } = [];

    public override async IAsyncEnumerable<IngestionChunk> ProcessAsync(
        IAsyncEnumerable<IngestionChunk> chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (IngestionChunk chunk in chunks.WithCancellation(cancellationToken))
        {
            Chunks.Add(chunk);
            yield return chunk;
        }
    }
}

sealed class FixtureExtractionClient(DocumentExtractionResult result) : IDocumentExtractionClient
{
    public const string EvidenceMarker = "EXTRACTION_ONLY_EVIDENCE";
    public const string ModelId = "fixture-extractor";
    public const string ProviderMarkdown = "# EXTRACTION_ONLY_MARKDOWN\n\nDuplicate provider rendering.";
    public const string ProviderResultKey = "provider.result";
    public const string ProviderPageKey = "provider.page";

    public DocumentExtractionResult SourceResult => result;
    public int ExtractCallCount { get; private set; }
    public string? LastMediaType { get; private set; }
    public DocumentExtractionOptions? LastOptions { get; private set; }

    public Task<DocumentExtractionResult> ExtractAsync(
        Stream document,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ExtractCallCount++;
        LastMediaType = mediaType;
        LastOptions = options;
        return Task.FromResult(result);
    }

    public IAsyncEnumerable<DocumentExtractionPageResult> ExtractPagesAsync(
        Stream document,
        string mediaType,
        DocumentExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    public static DocumentExtractionResult CreateScenario()
        => new(
        [
            new DocumentPage(
                1,
                [
                    new DocumentBlock("Quarterly Review")
                    {
                        Kind = DocumentBlockKind.Title,
                        Confidence = 0.99,
                        BoundingRegion = DocumentBoundingRegion.FromRectangle(1, 0, 0, 8, 1),
                        RawRepresentation = new object(),
                        AdditionalProperties = new() { ["provider.trace"] = EvidenceMarker },
                    },
                    new DocumentBlock("Revenue increased after the bridge rollout.")
                    {
                        Kind = DocumentBlockKind.Paragraph,
                    },
                    new DocumentTable(
                        2,
                        2,
                        [
                            SourceCell(0, 0, "Metric", DocumentTableCellKind.ColumnHeader),
                            SourceCell(0, 1, "Value", DocumentTableCellKind.ColumnHeader),
                            SourceCell(1, 0, "Revenue", DocumentTableCellKind.RowHeader),
                            SourceCell(1, 1, "$12M", DocumentTableCellKind.Content),
                        ])
                    {
                        RawRepresentation = new object(),
                        AdditionalProperties = new() { ["provider.table"] = EvidenceMarker },
                    },
                    new DocumentImage
                    {
                        Content = new byte[] { 1, 2, 3, 4 },
                        MediaType = "image/png",
                        Caption = "Quarterly revenue chart",
                        RawRepresentation = new object(),
                        AdditionalProperties = new() { ["provider.image"] = EvidenceMarker },
                    },
                    new DocumentBlock("provider-specific note") { Kind = new("provider-note") },
                ],
                ProviderMarkdown)
            {
                Dimensions = new(8, 11),
                CoordinateUnit = DocumentCoordinateUnit.Inch,
                RawRepresentation = new object(),
                AdditionalProperties = new() { [ProviderPageKey] = EvidenceMarker },
            },
            new DocumentPage(
                2,
                [
                    new DocumentBlock("Appendix") { Kind = DocumentBlockKind.Title },
                    new DocumentBlock("Retention policy remains unchanged.")
                    {
                        Kind = DocumentBlockKind.Paragraph,
                    },
                ]),
        ])
        {
            RawRepresentation = new object(),
            AdditionalProperties = new()
            {
                ["modelId"] = ModelId,
                [ProviderResultKey] = EvidenceMarker,
            },
        };

    private static DocumentTableCell SourceCell(
        int row,
        int column,
        string text,
        DocumentTableCellKind kind)
        => new(row, column, [new DocumentBlock(text)])
        {
            Kind = kind,
            Confidence = 0.95,
            RawRepresentation = new object(),
            AdditionalProperties = new() { ["provider.cell"] = EvidenceMarker },
        };
}

sealed class Preview2ChunkRecord : IngestionChunkVectorRecord
{
    [VectorStoreVector(RecordingEmbeddingGenerator.DimensionCount)]
    public override AIContent? Embedding => Content;
}

sealed class RecordingEmbeddingGenerator : IEmbeddingGenerator<AIContent, Embedding<float>>
{
    public const int DimensionCount = 4;
    public List<Type> InputTypes { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<AIContent> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<Embedding<float>> result = [];
        foreach (AIContent value in values)
        {
            InputTypes.Add(value.GetType());
            string text = value is TextContent content ? content.Text.ToUpperInvariant() : "";
            result.Add(new(new float[]
            {
                text.Contains("QUARTERLY", StringComparison.Ordinal) ? 1F : 0F,
                text.Contains("REVENUE", StringComparison.Ordinal) ? 1F : 0F,
                text.Contains("RETENTION", StringComparison.Ordinal) ? 1F : 0F,
                value is DataContent ? 1F : 0F,
            }));
        }
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(result));
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

readonly record struct MixedResult(int TextTokens);
