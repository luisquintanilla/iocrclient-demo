using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Documents;
using SharedDocument = Microsoft.Extensions.Documents.Document;

const string ExpectedSourceCommit = "6f7f3fa75d08599eb5005a0cd3db17d20694e1a8";
const string ExpectedPackageVersion = "10.8.0-preview2neutral.6f7f3fa";
const string ExpectedProjection =
    "Quarterly Review\n\nRevenue increased after the bridge rollout.\n\nMetric\tValue\nRevenue\t$12M\n\nQuarterly revenue chart\n\nprovider-specific note\n\nAppendix\n\nRetention policy remains unchanged.";

SharedDocument document = CreateDocument();
string provenancePath = FindRepoFile(Path.Combine("local-feed", "provenance.json"));
using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(provenancePath));
string sourceCommit = provenance.RootElement.GetProperty("implementationCommit").GetString() ?? string.Empty;
Require(sourceCommit == ExpectedSourceCommit, "The Documents-only proof must use the exact pinned source.");

string[] referencedProducts = typeof(SharedDocument).Assembly
    .GetReferencedAssemblies()
    .Select(static assembly => assembly.Name ?? string.Empty)
    .Where(static name =>
        name.Contains("DocumentExtraction", StringComparison.Ordinal) ||
        name.Contains("DataIngestion", StringComparison.Ordinal))
    .ToArray();
Require(referencedProducts.Length == 0, "The neutral Documents assembly must not reference extraction or MEDI.");
string packagePath = FindRepoFile(Path.Combine(
    "local-feed",
    $"Microsoft.Extensions.Documents.Abstractions.{ExpectedPackageVersion}.nupkg"));
string[] packageDependencies;
using (ZipArchive archive = ZipFile.OpenRead(packagePath))
{
    ZipArchiveEntry nuspecEntry = archive.Entries.Single(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    XDocument nuspec = XDocument.Load(nuspecEntry.Open());
    XNamespace ns = nuspec.Root!.Name.Namespace;
    packageDependencies = nuspec
        .Descendants(ns + "dependency")
        .Select(dependency => dependency.Attribute("id")?.Value)
        .Where(id => id is not null)
        .Distinct()
        .Cast<string>()
        .ToArray();
    Require(packageDependencies.All(static dependency => dependency == "System.Text.Json"),
        "The neutral Documents package must not add dependencies beyond System.Text.Json.");
}

string[] nodeTypes = document.Nodes
    .Select(static node => node.GetType().Name)
    .Distinct()
    .ToArray();
Require(
    nodeTypes.SequenceEqual(
        ["DocumentContainer", "DocumentText", "DocumentTable", "DocumentTableCell", "DocumentImage", "DocumentOpaque"]),
    "The Documents-only fixture shape changed.");

int[] pages = document.Nodes
    .SelectMany(static node => node.PageReferences)
    .Select(static reference => reference.PageNumber)
    .Distinct()
    .Order()
    .ToArray();
Require(pages.SequenceEqual([1, 2]), "The Documents-only fixture must retain both pages.");
Require(document.Text == ExpectedProjection, "The deterministic text projection changed.");

JsonSerializerOptions jsonOptions = new()
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
};
string json = JsonSerializer.Serialize(document, jsonOptions);
string[] discriminators = ["container", "text", "table", "image", "opaque"];
Require(
    discriminators.All(discriminator =>
        json.Contains($"\"$type\":\"{discriminator}\"", StringComparison.Ordinal)),
    "The serialized tree must contain every expected node discriminator.");
SharedDocument roundTrip = JsonSerializer.Deserialize<SharedDocument>(json, jsonOptions)
    ?? throw new InvalidOperationException("The Documents-only tree did not deserialize.");
DocumentOpaque opaque = roundTrip.Nodes.OfType<DocumentOpaque>().Single();
Require(roundTrip.Text == ExpectedProjection
        && opaque.LogicalKind == "provider.unknown-chart"
        && opaque.SchemaVersion == 2
        && opaque.Position == 2
        && opaque.PageReferences.Select(reference => reference.PageNumber)
            .SequenceEqual([2, 1, 2])
        && opaque.Payload.GetProperty("value").GetInt32() == 42,
    "Opaque semantic content did not retain identity, position, payload, or page references.");

Console.WriteLine($"documents-only source: luisquintanilla/extensions@{sourceCommit}");
Console.WriteLine("dependencies: Microsoft.Extensions.Documents.Abstractions only");
Console.WriteLine($"package dependencies: {(packageDependencies.Length == 0 ? "none (net10.0)" : string.Join(", ", packageDependencies))}");
Console.WriteLine($"node types: {string.Join(", ", nodeTypes)}");
Console.WriteLine($"pages: {string.Join(", ", pages)}");
Console.WriteLine($"projection: {document.Text.Replace("\n", " | ", StringComparison.Ordinal)}");
Console.WriteLine($"serialized discriminators: {string.Join(", ", discriminators)}");
Console.WriteLine("PASS: construct -> traverse -> project -> serialize without DocumentExtraction or MEDI");

static SharedDocument CreateDocument()
{
    DocumentText heading = Text("review-heading", "Quarterly Review", DocumentTextRole.Heading, page: 1);
    DocumentText revenue = Text("revenue-paragraph", "Revenue increased after the bridge rollout.", page: 1);
    DocumentTable table = new(
        new("revenue-table"),
        2,
        2,
        [
            Cell("metric-header", 0, 0, "Metric", DocumentTableCellRole.ColumnHeader),
            Cell("value-header", 0, 1, "Value", DocumentTableCellRole.ColumnHeader),
            Cell("revenue-label", 1, 0, "Revenue", DocumentTableCellRole.RowHeader),
            Cell("revenue-value", 1, 1, "$12M"),
        ],
        pageReferences: [new(1)]);
    DocumentImage image = new(
        new("revenue-chart"),
        new byte[] { 1, 2, 3, 4 },
        "image/png",
        description: "Quarterly revenue chart",
        pageReferences: [new(1)]);
    using JsonDocument opaquePayload = JsonDocument.Parse(
        """{"provider":"fixture","kind":"unknown-chart","value":42}""");
    DocumentOpaque opaque = new(
        new("opaque-chart"),
        "provider.unknown-chart",
        schemaVersion: 2,
        position: 2,
        opaquePayload.RootElement,
        pageReferences: [new(2), new(1), new(2)]);
    DocumentText providerNote = Text("provider-note", "provider-specific note", page: 1);
    DocumentText appendix = Text("appendix-heading", "Appendix", DocumentTextRole.Heading, page: 2);
    DocumentText retention = Text("retention-paragraph", "Retention policy remains unchanged.", page: 2);

    return new SharedDocument(
    [
        Section("page-1", 1, [heading, revenue, table, image, opaque, providerNote]),
        Section("page-2", 2, [appendix, retention]),
    ]);
}

static DocumentContainer Section(string id, int page, IReadOnlyList<DocumentNode> children) =>
    new(new(id), DocumentContainerRole.Section, children, pageReferences: [new(page)]);

static DocumentText Text(
    string id,
    string value,
    DocumentTextRole role = DocumentTextRole.Paragraph,
    int page = 1) =>
    new(new(id), value, role, pageReferences: [new(page)]);

static DocumentTableCell Cell(
    string id,
    int row,
    int column,
    string value,
    DocumentTableCellRole role = DocumentTableCellRole.Content) =>
    new(
        new(id),
        row,
        column,
        [new DocumentText(new($"{id}-text"), value, pageReferences: [new(1)])],
        role: role,
        pageReferences: [new(1)]);

static string FindRepoFile(string relativePath)
{
    DirectoryInfo? directory = new(Environment.CurrentDirectory);
    while (directory is not null)
    {
        string candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate))
        {
            return candidate;
        }
        directory = directory.Parent;
    }
    throw new FileNotFoundException($"Could not find {relativePath} from {Environment.CurrentDirectory}.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
