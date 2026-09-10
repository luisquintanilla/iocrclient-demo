#:project ocr-shape/OcrShape.csproj
#:package Microsoft.ML.Tokenizers.Data.O200kBase@1.0.3
#:package Microsoft.ML.Tokenizers.Data.Cl100kBase@1.0.3
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
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using SharedDocument = Microsoft.Extensions.Documents.Document;

const string ImplementationSha = "704a3e44ef4d7b053748780549fc2c8e929a444b";
const string PresentationSha = "7e5172fe81b9c2e1fb5db9d54c0ab761cd7be9f2";
const string PackageVersion = "10.8.0-preview2neutral.704a3e4";
VerifyFeedAndLoadedAssemblies(ImplementationSha, PackageVersion);
CheckNonGenericContracts();
Check(
    FoundryMistralOcrClient.IsMarkdownTableSeparator("| --- | --- |")
    && FoundryMistralOcrClient.IsMarkdownTableSeparator("--- | ---")
    && FoundryMistralOcrClient.IsMarkdownTableSeparator("| --- |")
    && !FoundryMistralOcrClient.IsMarkdownTableSeparator("Metric | Value"),
    "Mistral table separator detection changed.");

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
        "preview2-neutral",
        RecordingEmbeddingGenerator.DimensionCount);
using var writer = new VectorStoreWriter<Preview2ChunkRecord>(collection);
using var pipeline = new IngestionPipeline(reader, chunker, writer);
pipeline.ChunkProcessors.Add(processor);

string fixturePath = Path.Combine(Path.GetTempPath(), $"preview2-neutral-{Guid.NewGuid():N}.pdf");
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
    "The built-in reader did not invoke the public fake client exactly once.");
Check(ReferenceEquals(client.SourceResult, extraction)
        && ReferenceEquals(ingestion.Document, extraction.Document),
    "The neutral reader did not pass through the ordered shared Document identity.");

DocumentTable table = extraction.Document.Nodes.OfType<DocumentTable>().Single();
DocumentImage image = extraction.Document.Nodes.OfType<DocumentImage>().Single();
Check(extraction.Document.Nodes.Select(node => node.Id.Value).SequenceEqual(
    [
        "page-1",
        "review-heading",
        "revenue-paragraph",
        "revenue-table",
        "metric-header",
        "metric-header-text",
        "value-header",
        "value-header-text",
        "revenue-label",
        "revenue-label-text",
        "revenue-value",
        "revenue-value-text",
        "revenue-chart",
        "provider-note",
        "page-2",
        "appendix-heading",
        "retention-paragraph",
    ]), "Shared node order or stable identity changed.");
Check(table.RowCount == 2
        && table.ColumnCount == 2
        && table.Cells.Count == 4
        && Cell(table, 0, 0, DocumentTableCellRole.ColumnHeader).Text == "Metric"
        && Cell(table, 0, 1, DocumentTableCellRole.ColumnHeader).Text == "Value"
        && Cell(table, 1, 0, DocumentTableCellRole.RowHeader).Text == "Revenue"
        && Cell(table, 1, 1, DocumentTableCellRole.Content).Text == "$12M",
    "The exact 2x2 table shape, roles, or values changed.");
Check(image.Content.Span.SequenceEqual(new byte[] { 1, 2, 3, 4 })
        && image.MediaType == "image/png"
        && image.Description == "Quarterly revenue chart",
    "The shared image changed bytes, media type, or caption.");
Check(extraction.Document.GetNode(new("provider-note")) is DocumentText
    {
        Text: "provider-specific note",
        Role: DocumentTextRole.Paragraph,
    }, "The provider-specific unknown kind was not retained as literal text.");

AssertEvidenceIsolation(extraction, ingestion);
await CheckStreamingUpdatesAsync(extraction);
await CheckStreamingCancellationAsync(extraction);
await CheckMarkdownBoundaryAsync();
int pdfPages = await CheckPdfPigContractAsync();
await CheckRecursiveAndOverlapAsync();

List<IngestionChunk> chunks = chunker.Chunks;
string expectedPage1 = string.Join(
    "\n",
    [
        "Quarterly Review",
        "Revenue increased after the bridge rollout.",
        "Metric\tValue",
        "Revenue\t$12M",
        "Quarterly revenue chart",
        "provider-specific note",
    ]);
const string ExpectedPage2 = "Appendix\nRetention policy remains unchanged.";
Check(chunks.Count == 2
        && processor.Chunks.SequenceEqual(chunks)
        && chunks.All(chunk => !chunk.HasMetadata)
        && chunks.All(chunk => chunk.Content is TextContent && chunk.TokenCount > 0)
        && chunks[0].Content is TextContent page1 && page1.Text == expectedPage1
        && chunks[1].Content is TextContent page2 && page2.Text == ExpectedPage2
        && chunks[0].Context == "Quarterly Review"
        && chunks[1].Context == "Appendix"
        && chunks[0].PageNumbers.SequenceEqual([1])
        && chunks[1].PageNumbers.SequenceEqual([2])
        && chunks[0].SourceNodeIds.Select(id => id.Value).SequenceEqual(
            [
                "review-heading",
                "revenue-paragraph",
                "revenue-table",
                "metric-header",
                "metric-header-text",
                "value-header",
                "value-header-text",
                "revenue-label",
                "revenue-label-text",
                "revenue-value",
                "revenue-value-text",
                "revenue-chart",
                "provider-note",
            ])
        && chunks[1].SourceNodeIds.Select(id => id.Value).SequenceEqual(
            ["appendix-heading", "retention-paragraph"]),
    "Chunks changed content, context, TokenCount, processor flow, source IDs, or pages.");

List<Preview2ChunkRecord> records = await collection
    .GetAsync(record => record.DocumentId == ingestion.Identifier, top: 10)
    .ToListAsync();
Check(records.Count == chunks.Count
        && Record(records, 1).Content is TextContent stored1 && stored1.Text == expectedPage1
        && Record(records, 1).Context == "Quarterly Review"
        && Record(records, 2).Content is TextContent stored2 && stored2.Text == ExpectedPage2
        && Record(records, 2).Context == "Appendix"
        && records.All(record => record.SerializedContent is not null
            && record.SerializedPageNumbers is "1" or "2"
            && !record.SerializedContent.Contains(FixtureExtractionClient.EvidenceMarker, StringComparison.Ordinal)
            && !record.SerializedContent.Contains(FixtureExtractionClient.ProviderMarkdown, StringComparison.Ordinal))
        && typeof(IngestionChunkVectorRecord).GetProperty("SourceNodeIds") is null,
    "Stock writer changed content, context, pages, or SourceNodeIds omission.");
Check(embeddings.InputTypes.SequenceEqual([typeof(TextContent), typeof(TextContent)]),
    "Configured provider did not embed both text records during upsert.");

Preview2ChunkRecord revenue = await FirstAsync(
    collection.SearchAsync(new TextContent("quarterly revenue"), top: 1));
Preview2ChunkRecord retention = await FirstAsync(
    collection.SearchAsync(new TextContent("retention unchanged"), top: 1));
Check(revenue.PageNumbers.SequenceEqual([1])
        && revenue.Content is TextContent revenueText && revenueText.Text == expectedPage1
        && retention.PageNumbers.SequenceEqual([2])
        && retention.Content is TextContent retentionText && retentionText.Text == ExpectedPage2
        && embeddings.InputTypes.SequenceEqual(
            [typeof(TextContent), typeof(TextContent), typeof(TextContent), typeof(TextContent)]),
    "Retrieval did not return exact page-specific content or invoke query embeddings.");

MixedResult mixed = await CheckMixedContentAsync();

Console.WriteLine($"Source: {ImplementationSha}");
Console.WriteLine($"Presentation: {PresentationSha} (evidence-only)");
Console.WriteLine("Feed: 6/6 hashes+id+version+repo+commit");
Console.WriteLine("Contracts: pipeline/chunker/processor/writer/chunk=non-generic content=AIContent TokenCount=required");
Console.WriteLine($"Client/pipeline: calls={client.ExtractCallCount} processors=1 processed_chunks={processor.Chunks.Count} pass_through=true");
Console.WriteLine("Shared: pages=1,2 nodes=17 table=2x2 image=4B unknown-kind=paragraph");
Console.WriteLine($"Chunks: count=2 types=TextContent|TextContent tokens={chunks[0].TokenCount}|{chunks[1].TokenCount} pages=1|2 source_ids=13|2");
Console.WriteLine("Writer: typed=Preview2ChunkRecord stored=2 pages=1|2 SourceNodeIds=not-persisted embeddings=TextContent,TextContent");
Console.WriteLine("Retrieval: revenue=page1:$12M retention=page2:unchanged");
Console.WriteLine($"Mixed: chunks=TextContent({mixed.TextTokens})|DataContent({mixed.DataTokens}) stored=2 roundtrip=true embeddings=TextContent,DataContent");
Console.WriteLine("Markdown: exact=extraction-only canonical-empty=true");
Console.WriteLine($"PdfPig: pages={pdfPages} recursive_provenance=true metadata=reader,page_count");
Console.WriteLine("Range/overlap: pages=1,2 recursive_source_ids=true trailing_overlap_chunk=false");
Console.WriteLine("Loss: extraction evidence retained; ingestion/chunks/records isolated");
Console.WriteLine("PASS: Preview 2 neutral shared tree -> non-generic pipeline -> typed writer -> provider embedding -> retrieval");

static DocumentText Cell(
    DocumentTable table,
    int row,
    int column,
    DocumentTableCellRole expectedRole)
{
    DocumentTableCell cell = table.Cells.Single(
        candidate => candidate.RowIndex == row && candidate.ColumnIndex == column);
    Check(cell.Role == expectedRole
            && cell.RowSpan == 1
            && cell.ColumnSpan == 1
            && cell.SourceNodeIds.SequenceEqual([cell.Id]),
        $"Cell ({row},{column}) role, span, or source identity changed.");
    return cell.Content.OfType<DocumentText>().Single();
}

static void AssertEvidenceIsolation(
    DocumentExtractionResult extraction,
    IngestionDocument ingestion)
{
    DocumentPage first = extraction.Pages[0];
    Check(first.Markdown == FixtureExtractionClient.ProviderMarkdown
            && first.RawRepresentation is not null
            && first.Dimensions is { Width: 8, Height: 11 }
            && first.CoordinateUnit == DocumentCoordinateUnit.Inch
            && first.AdditionalProperties?[FixtureExtractionClient.ProviderPageKey] as string
                == FixtureExtractionClient.EvidenceMarker
            && extraction.RawRepresentation is not null
            && extraction.AdditionalProperties?[FixtureExtractionClient.ProviderResultKey] as string
                == FixtureExtractionClient.EvidenceMarker
            && first.Evidence.Count == 4
            && first.Evidence.All(evidence => evidence.RawRepresentation is not null)
            && first.Evidence.Any(evidence =>
                evidence.NodeId == new DocumentNodeId("review-heading")
                && evidence.Confidence == 0.99
                && evidence.BoundingRegion?.GetBounds() is { Left: 0, Top: 0, Right: 8, Bottom: 1 })
            && first.Evidence.Any(evidence =>
                evidence.NodeId == new DocumentNodeId("metric-header-text")
                && evidence.Confidence == 0.95),
        "Extraction-only evidence was not retained at the source.");
    Check(ReferenceEquals(ingestion.Document, extraction.Document)
            && !ingestion.HasMetadata
            && !ingestion.Document.Text.Contains(FixtureExtractionClient.EvidenceMarker, StringComparison.Ordinal)
            && !ingestion.Document.Text.Contains(FixtureExtractionClient.ProviderMarkdown, StringComparison.Ordinal),
        "Extraction evidence or duplicate Markdown leaked into ingestion.");
}

static async Task CheckMarkdownBoundaryAsync()
{
    const string Markdown = "## Supplement\n\nExact **provider Markdown**.";
    DocumentExtractionResult markdownOnly = new(
        [new DocumentPage(4, new SharedDocument([]), Markdown)]);
    Check(markdownOnly.Pages.Single().Markdown == Markdown
            && markdownOnly.Pages.Single().Text == string.Empty
            && markdownOnly.Text == string.Empty,
        "Markdown-only source unexpectedly became canonical Text.");
    using var client = new FixtureExtractionClient(markdownOnly);
    IngestionDocument ingestion = await new DocumentExtractionReader(client).ReadAsync(
        new MemoryStream([4]), "markdown-only.pdf", "application/pdf");
    Check(ReferenceEquals(ingestion.Document, markdownOnly.Document)
            && ingestion.Document.Text == string.Empty,
        "Neutral reader changed the extraction-only Markdown policy.");
}

static async Task CheckStreamingUpdatesAsync(DocumentExtractionResult extraction)
{
    List<DocumentExtractionPageResult> updates = await
        DocumentExtractionDemoExtensions.StreamAsUpdates(_ => Task.FromResult(extraction))
            .ToListAsync();
    Check(updates.Count == 2
            && updates[0].PagesProcessed == 1
            && updates[0].TotalPages == 2
            && updates[0].Usage is null
            && updates[0].RawRepresentation is null
            && updates[0].AdditionalProperties is null
            && updates[1].PagesProcessed == 2
            && updates[1].TotalPages == 2
            && ReferenceEquals(updates[1].Usage, extraction.Usage)
            && ReferenceEquals(updates[1].RawRepresentation, extraction.RawRepresentation)
            && ReferenceEquals(updates[1].AdditionalProperties, extraction.AdditionalProperties),
        "Streaming updates changed progress or terminal usage/raw/property propagation.");
}

static async Task CheckStreamingCancellationAsync(DocumentExtractionResult extraction)
{
    using var cancellation = new CancellationTokenSource();
    await using IAsyncEnumerator<DocumentExtractionPageResult> updates =
        DocumentExtractionDemoExtensions.StreamAsUpdates(
            _ => Task.FromResult(extraction),
            cancellation.Token)
        .GetAsyncEnumerator();
    Check(await updates.MoveNextAsync(), "Streaming cancellation fixture produced no first page.");
    cancellation.Cancel();
    try
    {
        _ = await updates.MoveNextAsync();
        throw new InvalidOperationException("Streaming adapter ignored cancellation between pages.");
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task<int> CheckPdfPigContractAsync()
{
    string path = Path.Combine("data", "survival-kit.pdf");
    await using FileStream stream = File.OpenRead(path);
    IngestionDocument document = await new PdfPigReader(policy: OcrPolicy.Never).ReadAsync(
        stream, Path.GetFileName(path), "application/pdf");
    int[] pages = document.Document.Nodes
        .SelectMany(node => node.PageReferences)
        .Select(reference => reference.PageNumber)
        .Distinct()
        .Order()
        .ToArray();
    Check(pages.Length > 0
            && document.Metadata["reader"] as string == "pdfpig-native"
            && (int)document.Metadata["page_count"]! == pages.Length
            && document.Document.Children.OfType<DocumentContainer>().All(section =>
                section.PageReferences.Count == 1
                && section.Children.All(child =>
                    child.PageReferences.SequenceEqual(section.PageReferences))),
        "PdfPig page metadata or recursive provenance changed.");
    return pages.Length;
}

static async Task CheckRecursiveAndOverlapAsync()
{
    DocumentTable nested = new(
        new("range-table"),
        1,
        1,
        [
            new DocumentTableCell(
                new("range-cell"),
                0,
                0,
                [
                    new DocumentText(new("alpha"), "alpha alpha alpha alpha alpha", pageReferences: [new(1)]),
                    new DocumentText(new("omega"), "omega omega omega omega omega", pageReferences: [new(2)]),
                ]),
        ]);
    IngestionDocument range = new("range", new SharedDocument([nested]));
    List<IngestionChunk> rangeChunks = await new DocumentTokenChunker(
        new(TiktokenTokenizer.CreateForModel("gpt-4"))
        {
            MaxTokensPerChunk = 4,
            OverlapTokens = 0,
        }).ProcessAsync(range).ToListAsync();
    Check(rangeChunks.Any(chunk =>
                chunk.SourceNodeIds.Contains(new("alpha"))
                && !chunk.SourceNodeIds.Contains(new("omega"))
                && chunk.PageNumbers.SequenceEqual([1]))
            && rangeChunks.Any(chunk =>
                chunk.SourceNodeIds.Contains(new("omega"))
                && !chunk.SourceNodeIds.Contains(new("alpha"))
                && chunk.PageNumbers.SequenceEqual([2])),
        "Recursive/range source and page provenance changed.");

    IngestionDocument overlap = new(
        "overlap",
        new SharedDocument(
            [new DocumentText(new("text"), "hello world", pageReferences: [new(1)])]));
    List<IngestionChunk> overlapChunks = await new DocumentTokenChunker(
        new(TiktokenTokenizer.CreateForModel("gpt-4"))
        {
            MaxTokensPerChunk = 2,
            OverlapTokens = 1,
        }).ProcessAsync(overlap).ToListAsync();
    Check(overlapChunks.Count == 1
            && overlapChunks[0].Content is TextContent text
            && text.Text == "hello world"
            && overlapChunks[0].TokenCount == 2,
        "Token chunker emitted an overlap-only terminal chunk.");
}

static async Task<MixedResult> CheckMixedContentAsync()
{
    DocumentText textNode = new(new("binary-text"), "Binary appendix", pageReferences: [new(1)]);
    DocumentImage imageNode = new(
        new("binary-image"),
        new byte[] { 9, 8, 7 },
        "image/png",
        pageReferences: [new(1)]);
    IngestionDocument document = new("mixed.pdf", new SharedDocument([textNode, imageNode]));
    Tokenizer tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
    var text = new IngestionChunk(
        new TextContent(textNode.Text),
        document,
        tokenizer.CountTokens(textNode.Text, considerNormalization: false),
        sourceNodeIds: textNode.SourceNodeIds,
        pageNumbers: [1]);
    var binary = new IngestionChunk(
        new DataContent(imageNode.Content, imageNode.MediaType!),
        document,
        tokenCount: 1,
        sourceNodeIds: imageNode.SourceNodeIds,
        pageNumbers: [1]);
    Check(text.TokenCount > 0
            && binary.TokenCount == 1
            && binary.Content is DataContent data
            && data.MediaType == "image/png"
            && data.Data.Span.SequenceEqual(new byte[] { 9, 8, 7 }),
        "Mixed AIContent or required TokenCount behavior changed.");

    using var embeddings = new RecordingEmbeddingGenerator();
    using var store = new InMemoryVectorStore(new() { EmbeddingGenerator = embeddings });
    VectorStoreCollection<Guid, Preview2ChunkRecord> collection =
        store.GetIngestionRecordCollection<Preview2ChunkRecord>(
            "mixed",
            RecordingEmbeddingGenerator.DimensionCount);
    using var writer = new VectorStoreWriter<Preview2ChunkRecord>(collection);
    await writer.WriteAsync(new[] { text, binary }.ToAsyncEnumerable());
    List<Preview2ChunkRecord> records = await collection.GetAsync(
        record => record.DocumentId == document.Identifier,
        top: 10).ToListAsync();
    Check(records.Count == 2
            && records.Any(record => record.Content is TextContent)
            && records.Any(record => record.Content is DataContent)
            && embeddings.InputTypes.SequenceEqual([typeof(TextContent), typeof(DataContent)]),
        "Stock writer/provider path did not persist and embed both AIContent types.");
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
                before.MediaType == after.MediaType
                && before.Data.Span.SequenceEqual(after.Data.Span),
            _ => false,
        };
        Check(sameContent && roundTrip.PageNumbers.SequenceEqual(record.PageNumbers),
            "Polymorphic AIContent/page-number serialization changed.");
    }
    Preview2ChunkRecord image = await FirstAsync(
        collection.SearchAsync(new DataContent(new byte[] { 9, 8, 7 }, "image/png"), top: 1));
    Check(image.Content is DataContent
            && image.PageNumbers.SequenceEqual([1])
            && embeddings.InputTypes.Last() == typeof(DataContent),
        "Provider contract did not accept a DataContent query embedding.");
    return new(text.TokenCount, binary.TokenCount);
}

static void CheckNonGenericContracts()
{
    Type[] contracts =
    [
        typeof(IngestionPipeline),
        typeof(IngestionChunker),
        typeof(IngestionChunkProcessor),
        typeof(IngestionChunkWriter),
        typeof(IngestionChunk),
    ];
    Check(contracts.All(type => !type.IsGenericType)
            && typeof(IngestionChunk).GetProperty(nameof(IngestionChunk.Content))?.PropertyType
                == typeof(AIContent)
            && typeof(IngestionChunk).GetProperty(nameof(IngestionChunk.TokenCount))?.PropertyType
                == typeof(int)
            && typeof(VectorStoreWriter<>).IsGenericTypeDefinition,
        "Preview 2 non-generic contracts or typed writer shape changed.");
}

static void VerifyFeedAndLoadedAssemblies(string implementationSha, string packageVersion)
{
    string feed = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "local-feed"));
    (string Id, string Hash, Assembly Assembly)[] packages =
    [
        ("Microsoft.Extensions.DataIngestion", "2b6002fc142dace6a5b08a1bc845eb544d08523c4f75d60c6384a36255e8f7b0", typeof(SectionChunker).Assembly),
        ("Microsoft.Extensions.DataIngestion.Abstractions", "6b8a88bb5f52121b05022c834de890669f8a8327a54bafa148df063675cf2f4f", typeof(IngestionChunk).Assembly),
        ("Microsoft.Extensions.DataIngestion.DocumentExtraction", "c2dd354bf6460b5f1f8b01186b5ff3f0c27ce790a6bb08535e30846250ca5d35", typeof(DocumentExtractionReader).Assembly),
        ("Microsoft.Extensions.DocumentExtraction", "fa54be131cc99b3c870ea9789cde03584967413302e2fa7ac53f9ac6e89b79a1", typeof(DocumentExtractionClientBuilder).Assembly),
        ("Microsoft.Extensions.DocumentExtraction.Abstractions", "a4347cb50702c82127af83cbcb5852d3148a2429f7920f13b89c0538b67e2b65", typeof(DocumentPage).Assembly),
        ("Microsoft.Extensions.Documents.Abstractions", "c94ea97233f9756009012f8f25234974f56c950982d7021b2df22430d4c98f4b", typeof(SharedDocument).Assembly),
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
                && repository.Attribute("url")?.Value == "https://github.com/dotnet/extensions.git"
                && repository.Attribute("commit")?.Value == implementationSha,
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
    => (await values.ToListAsync()).Single();

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
    {
        DocumentText heading = Text("review-heading", "Quarterly Review", DocumentTextRole.Heading, 1);
        DocumentText revenue = Text("revenue-paragraph", "Revenue increased after the bridge rollout.", page: 1);
        DocumentTable table = new(
            new("revenue-table"),
            2,
            2,
            [
                SourceCell("metric-header", 0, 0, "Metric", DocumentTableCellRole.ColumnHeader),
                SourceCell("value-header", 0, 1, "Value", DocumentTableCellRole.ColumnHeader),
                SourceCell("revenue-label", 1, 0, "Revenue", DocumentTableCellRole.RowHeader),
                SourceCell("revenue-value", 1, 1, "$12M", DocumentTableCellRole.Content),
            ],
            pageReferences: [new(1)]);
        DocumentImage image = new(
            new("revenue-chart"),
            new byte[] { 1, 2, 3, 4 },
            "image/png",
            description: "Quarterly revenue chart",
            pageReferences: [new(1)]);
        DocumentText note = Text("provider-note", "provider-specific note", page: 1);
        DocumentText appendix = Text("appendix-heading", "Appendix", DocumentTextRole.Heading, 1, 2);
        DocumentText retention = Text("retention-paragraph", "Retention policy remains unchanged.", page: 2);
        DocumentPage page1 = new(
            1,
            new SharedDocument([Section("page-1", 1, [heading, revenue, table, image, note])]),
            ProviderMarkdown,
            [
                Evidence(heading.Id, 0.99),
                Evidence(table.Id),
                Evidence(new("metric-header-text"), 0.95),
                Evidence(image.Id),
            ])
        {
            Dimensions = new(8, 11),
            CoordinateUnit = DocumentCoordinateUnit.Inch,
            RawRepresentation = new object(),
            AdditionalProperties = new() { [ProviderPageKey] = EvidenceMarker },
        };
        DocumentPage page2 = new(
            2,
            new SharedDocument([Section("page-2", 2, [appendix, retention])]));
        return new([page1, page2])
        {
            Usage = new() { PagesProcessed = 2 },
            RawRepresentation = new object(),
            AdditionalProperties = new()
            {
                ["modelId"] = ModelId,
                [ProviderResultKey] = EvidenceMarker,
            },
        };
    }

    private static DocumentContainer Section(
        string id,
        int page,
        IReadOnlyList<DocumentNode> children)
        => new(new(id), DocumentContainerRole.Section, children, pageReferences: [new(page)]);

    private static DocumentText Text(
        string id,
        string value,
        DocumentTextRole role = DocumentTextRole.Paragraph,
        int? level = null,
        int page = 1)
        => new(new(id), value, role, level, pageReferences: [new(page)]);

    private static DocumentTableCell SourceCell(
        string id,
        int row,
        int column,
        string value,
        DocumentTableCellRole role)
        => new(
            new(id),
            row,
            column,
            [new DocumentText(new($"{id}-text"), value, pageReferences: [new(1)])],
            role: role,
            pageReferences: [new(1)]);

    private static DocumentExtractionEvidence Evidence(
        DocumentNodeId id,
        double? confidence = null)
        => new(id)
        {
            Confidence = confidence,
            BoundingRegion = DocumentBoundingRegion.FromRectangle(1, 0, 0, 8, 1),
            RawRepresentation = new object(),
            AdditionalProperties = new() { ["provider.trace"] = EvidenceMarker },
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

readonly record struct MixedResult(int TextTokens, int DataTokens);
