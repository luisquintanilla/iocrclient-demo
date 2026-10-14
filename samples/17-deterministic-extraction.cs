#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
// 17-deterministic-extraction.cs — credential-free contract and composition proof.
//
// This is intentionally in-memory first. It exercises the provisional Documents builder and
// consumer adapters without serializing or publishing the old neutral-tree shape.
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;
using DemoOcr;

int checks = 0;

using (DocumentExtractionResult distinct = new[]
{
    new DocumentExtractionPage(1, [Text("a", "same", 1), Text("b", "same", 0)]),
}.ToDocumentExtractionResult("distinct-text"))
{
    Check(distinct.EnumerateElements().Count() == 2, "equal text keeps distinct observations");
}

ExpectThrows<ArgumentException>(
    () => new[]
    {
        new DocumentExtractionPage(1, [Text("duplicate", "same", 0), Text("duplicate", "same", 1)]),
    }.ToDocumentExtractionResult("duplicate-provider-id"),
    "repeated provider IDs are rejected");

ExpectThrows<ArgumentException>(
    () => new[]
    {
        new DocumentExtractionPage(1, []),
        new DocumentExtractionPage(1, []),
    }.ToDocumentExtractionResult("duplicate-page"),
    "duplicate page numbers are rejected");

DocumentPageReference firstReference = new("opaque-page");
DocumentPageReference secondReference = new("opaque-page");
DocumentExtractionEvidence evidence = new(new DocumentEvidenceReference("opaque-evidence"));
using (DocumentExtractionResult associations = new[]
{
    new DocumentExtractionPage(
        1,
        [new DocumentExtractionText(
            "associated",
            0,
            DocumentTextKind.Paragraph,
            "evidence-bound",
            pageReferences: [firstReference, secondReference],
            evidence: [evidence])],
        pageReferences: [firstReference, secondReference]),
}.ToDocumentExtractionResult("association-order"))
{
    DocumentTextElement element = associations.EnumerateElements().OfType<DocumentTextElement>().Single();
    Check(associations.Pages[0].PageReferences.Count == 2, "duplicate page references remain on the page");
    Check(element.PageReferences.Count == 4, "page and observation references remain ordered and opaque");
    Check(element.Evidence.Count == 1 && element.Evidence[0].Value == "opaque-evidence", "evidence remains an association");
}

var artifact = new DocumentArtifact("image/png", new MemoryStream([1, 2, 3]));
DocumentExtractionImage image = new(
    "image",
    0,
    "[image]",
    content: new byte[] { 1, 2, 3 },
    mediaType: "image/png",
    artifacts: [artifact]);
using (DocumentExtractionResult owned = new[]
{
    new DocumentExtractionPage(
        1,
        [
            new DocumentExtractionTable("table", 1, "[table]", 1, 1),
            image,
            new DocumentExtractionExtension("extension", 2, "opaque-type", "[extension]"),
        ]),
}.ToDocumentExtractionResult("elements-and-ownership"))
{
    Check(owned.CountElements<DocumentTableElement>() == 1, "tables finalize into Documents elements");
    Check(owned.CountElements<DocumentImageElement>() == 1, "images finalize into Documents elements");
    Check(owned.CountElements<DocumentExtensionElement>() == 1, "extensions remain opaque Documents elements");
}
Check(artifact.IsDisposed, "document disposal owns extraction artifacts");

Check(FoundryMistralOcrClient.FindUnique("alpha beta", "beta") is { Start: 6, Length: 4 },
    "unique Mistral replacement is located");
Check(FoundryMistralOcrClient.FindUnique("same same", "same") is null,
    "ambiguous Mistral replacement is not applied");
ExpectThrows<ArgumentOutOfRangeException>(
    () => new DocumentSourcePage("missing-page", 0),
    "unknown source pages must not be fabricated as page one");
Check(VisionLlmOcrClient.CanFallbackToFreeform(
        new InvalidOperationException("response format schema is not supported")),
    "VLM schema rejection may fall back");
Check(!VisionLlmOcrClient.CanFallbackToFreeform(new InvalidOperationException("quota exceeded")),
    "VLM quota failures do not fall back");
Check(!VisionLlmOcrClient.CanFallbackToFreeform(new OperationCanceledException()),
    "VLM cancellation does not fall back");

var fake = new DeterministicExtractionClient();
var reader = new PdfPigReader(fake, OcrPolicy.AllPages);
await using (FileStream source = File.OpenRead("samples/data/survival-kit.pdf"))
{
    using Document document = await reader.ReadAsync(
        source,
        "pdfpig-proof",
        "application/pdf");
    Check(fake.ExtractCalls == 1 && document.Sections.Count == 1,
        "PdfPig whole-document ownership routes through the injected client");
}
Check(!fake.IsDisposed, "PdfPig reader does not dispose the injected client");
fake.Dispose();

Console.WriteLine($"deterministic checks: {checks} passed");
return 0;

static DocumentExtractionText Text(string providerId, string value, int order)
    => new(providerId, order, DocumentTextKind.Paragraph, value, value);

void Check(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException($"FAILED: {description}");
    }

    checks++;
}

static void ExpectThrows<TException>(Action action, string description)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"FAILED: {description}");
}

sealed class DeterministicExtractionClient : IDocumentExtractionClient
{
    public int ExtractCalls { get; private set; }
    public bool IsDisposed { get; private set; }

    public async IAsyncEnumerable<DocumentExtractionPage> ExtractPagesAsync(
        Stream document,
        string identifier,
        string? mediaType,
        DocumentExtractionOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ExtractCalls++;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new DocumentExtractionPage(
            1,
            [new DocumentExtractionText("fake:text", 0, DocumentTextKind.Paragraph, "proof", "proof")],
            "proof");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => IsDisposed = true;
}
