using System.Text.Json;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Documents;
using SharedDocument = Microsoft.Extensions.Documents.Document;

const string ExpectedSourceCommit = "704a3e44ef4d7b053748780549fc2c8e929a444b";
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
    "Microsoft.Extensions.Documents.Abstractions.10.8.0-preview2neutral.704a3e4.nupkg"));
using (ZipArchive archive = ZipFile.OpenRead(packagePath))
{
    ZipArchiveEntry nuspecEntry = archive.Entries.Single(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
    XDocument nuspec = XDocument.Load(nuspecEntry.Open());
    XNamespace ns = nuspec.Root!.Name.Namespace;
    string[] packageDependencies = nuspec
        .Descendants(ns + "dependency")
        .Select(dependency => dependency.Attribute("id")?.Value)
        .Where(id => id is not null)
        .Distinct()
        .Cast<string>()
        .ToArray();
    Require(packageDependencies.SequenceEqual(["System.Text.Json"]),
        "The neutral Documents package must depend only on System.Text.Json.");
}

string[] nodeTypes = document.Nodes
    .Select(static node => node.GetType().Name)
    .Distinct()
    .ToArray();
Require(
    nodeTypes.SequenceEqual(
        ["DocumentContainer", "DocumentText", "DocumentTable", "DocumentTableCell", "DocumentImage"]),
    "The Documents-only fixture shape changed.");

int[] pages = document.Nodes
    .SelectMany(static node => node.PageReferences)
    .Select(static reference => reference.PageNumber)
    .Distinct()
    .Order()
    .ToArray();
Require(pages.SequenceEqual([1, 2]), "The Documents-only fixture must retain both pages.");
Require(document.Text == ExpectedProjection, "The deterministic text projection changed.");

string json = JsonSerializer.Serialize(document);
string[] discriminators = ["container", "text", "table", "image"];
Require(
    discriminators.All(discriminator =>
        json.Contains($"\"$type\":\"{discriminator}\"", StringComparison.Ordinal)),
    "The serialized tree must contain every expected node discriminator.");

Console.WriteLine($"documents-only source: luisquintanilla/extensions@{sourceCommit}");
Console.WriteLine("dependencies: Microsoft.Extensions.Documents.Abstractions only");
Console.WriteLine("package dependencies: System.Text.Json only");
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
    DocumentText providerNote = Text("provider-note", "provider-specific note", page: 1);
    DocumentText appendix = Text("appendix-heading", "Appendix", DocumentTextRole.Heading, page: 2);
    DocumentText retention = Text("retention-paragraph", "Retention policy remains unchanged.", page: 2);

    return new SharedDocument(
    [
        Section("page-1", 1, [heading, revenue, table, image, providerNote]),
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
