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
    IDocumentExtractionClient ocrClient)
{
    public async Task IngestDataAsync(DirectoryInfo directory, string searchPattern)
    {
        using var writer = new VectorStoreWriter<IngestedChunk>(vectorCollection);

        using var pipeline = new IngestionPipeline(
            reader: new DocumentReader(directory, ocrClient),
            chunker: new SemanticSimilarityChunker(
                embeddingGenerator.AsTextContentEmbeddingGenerator(),
                new(TiktokenTokenizer.CreateForModel("gpt-4o"))),
            writer: writer,
            loggerFactory: loggerFactory);

        await foreach (var result in pipeline.ProcessAsync(directory, searchPattern))
        {
            logger.LogInformation("Completed processing '{id}'. Succeeded: '{succeeded}'.", result.DocumentId, result.Succeeded);
        }
    }
}
