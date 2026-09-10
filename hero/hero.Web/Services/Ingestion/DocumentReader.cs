using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;
using SharedDocument = Microsoft.Extensions.Documents.Document;

namespace hero.Web.Services.Ingestion;

internal sealed class DocumentReader(DirectoryInfo rootDirectory, IDocumentExtractionClient ocrClient) : IngestionDocumentReader
{
    private readonly DocumentExtractionReader _ocrReader = new(ocrClient, new DocumentExtractionOptions
    {
        AdditionalProperties = new()
        {
            [VisionLlmOcrClient.StructuredKey] = true,
        },
    });

    public override Task<IngestionDocument> ReadAsync(FileInfo source, string identifier, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        if (Path.IsPathFullyQualified(identifier))
        {
            // Normalize the identifier to its relative path
            identifier = Path.GetRelativePath(rootDirectory.FullName, identifier);
        }

        mediaType = GetCustomMediaType(source) ?? mediaType;
        return base.ReadAsync(source, identifier, mediaType, cancellationToken);
    }

    public override Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
        => mediaType switch
        {
            "application/pdf" => _ocrReader.ReadAsync(source, identifier, mediaType, cancellationToken),
            "text/markdown" => ReadMarkdownAsync(source, identifier, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported media type '{mediaType}'"),
        };

    private static string? GetCustomMediaType(FileInfo source)
        => source.Extension switch
        {
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            _ => null
        };

    private static async Task<IngestionDocument> ReadMarkdownAsync(Stream source, string identifier, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return new IngestionDocument(
            identifier,
            new SharedDocument(
            [
                new DocumentContainer(
                    new("markdown-section"),
                    DocumentContainerRole.Section,
                    [new DocumentText(new("markdown-text"), text)]),
            ]));
    }
}
