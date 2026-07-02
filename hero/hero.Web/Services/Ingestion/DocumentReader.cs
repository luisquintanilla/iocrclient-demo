using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;

namespace hero.Web.Services.Ingestion;

internal sealed class DocumentReader(DirectoryInfo rootDirectory, IOcrClient ocrClient) : IngestionDocumentReader
{
    private readonly OcrDocumentReader _ocrReader = new(ocrClient, new OcrOptions
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

        var document = new IngestionDocument(identifier);
        var section = new IngestionDocumentSection();
        section.Elements.Add(new IngestionDocumentParagraph(text)
        {
            Text = text,
        });
        document.Sections.Add(section);

        return document;
    }
}
