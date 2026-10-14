using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;

namespace hero.Web.Services.Ingestion;

public class DataIngestor(
    ILogger<DataIngestor> logger,
    ILoggerFactory loggerFactory,
    VectorStoreCollection<Guid, IngestedChunk> vectorCollection,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IDocumentExtractionClient ocrClient,
    IConfiguration configuration)
{
    public async Task IngestDataAsync(DirectoryInfo directory, string searchPattern, CancellationToken cancellationToken = default)
    {
        using var writer = new CitationVectorStoreWriter(vectorCollection);

        using var pipeline = new IngestionPipeline(
            chunker: new SemanticSimilarityChunker(
                embeddingGenerator.AsTextContentEmbeddingGenerator(),
                new(TiktokenTokenizer.CreateForModel("gpt-4o"))),
            writer: writer,
            loggerFactory: loggerFactory);

        int failed = 0;
        await foreach (var result in pipeline.ProcessAsync(
            new DocumentReader(directory, ocrClient, ExtractionProviderFactory.CreateOptions(configuration)), directory, searchPattern,
            cancellationToken: cancellationToken))
        {
            logger.LogInformation("Completed processing '{id}'. Succeeded: '{succeeded}'.", result.DocumentId, result.Succeeded);
            if (!result.Succeeded)
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            throw new InvalidOperationException($"Ingestion failed for {failed} document(s). See the ingestion error logs.");
        }
    }
}
