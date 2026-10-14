using DemoOcr;
using System.Security.Cryptography;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.Documents;

namespace hero.Web.Services.Ingestion;

internal sealed class DocumentReader(DirectoryInfo rootDirectory, IDocumentExtractionClient ocrClient,
    DocumentExtractionOptions? options = null) : IngestionDocumentReader
{
    private readonly DocumentExtractionReader _ocrReader = new(ocrClient, options);

    public override Task<Document> ReadAsync(FileInfo source, string identifier, string? mediaType = null, CancellationToken cancellationToken = default)
    {
        if (Path.IsPathFullyQualified(identifier))
        {
            // Normalize the identifier to its relative path
            identifier = Path.GetRelativePath(rootDirectory.FullName, identifier);
        }

        mediaType = GetCustomMediaType(source) ?? mediaType;
        return base.ReadAsync(source, identifier, mediaType, cancellationToken);
    }

    public override async Task<Document> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        string sourceHash = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)))).ToLowerInvariant();
        buffer.Position = 0;
        Document document = mediaType switch
        {
            "application/pdf" => await _ocrReader.ReadAsync(buffer, identifier, mediaType, cancellationToken).ConfigureAwait(false),
            "text/markdown" => await ReadMarkdownAsync(buffer, identifier, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported media type '{mediaType}'"),
        };
        document.Metadata["demo.source.sha256"] = sourceHash;
        return document;
    }

    private static string? GetCustomMediaType(FileInfo source)
        => source.Extension switch
        {
            ".pdf" => "application/pdf",
            ".md" => "text/markdown",
            _ => null
        };

    private static async Task<Document> ReadMarkdownAsync(Stream source, string identifier, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, leaveOpen: true);
        string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var document = new Document(identifier);
        var section = new DocumentSection(text);
        section.Elements.Add(new DocumentTextElement(DocumentTextKind.Paragraph, text, text));
        document.Sections.Add(section);

        return document;
    }
}
