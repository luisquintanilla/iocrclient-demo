using DemoOcr;
using Microsoft.Extensions.AI;
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
    IOcrClient ocrClient)
{
    public async Task IngestDataAsync(DirectoryInfo directory, string searchPattern)
    {
        using var writer = new VectorStoreWriter<IngestedChunk>(vectorCollection, new()
        {
            IncrementalIngestion = false,
        });

        using var pipeline = new IngestionPipeline(
            reader: new DocumentReader(directory, ocrClient),
            chunker: new SemanticSimilarityChunker(
                embeddingGenerator.AsTextContentEmbeddingGenerator(),
                new(TiktokenTokenizer.CreateForModel("gpt-4o"))
                {
                    MetadataKeysToPropagate = new HashSet<string> { "page_number", "ocr_source" },
                }),
            writer: writer,
            loggerFactory: loggerFactory);

        await foreach (var result in pipeline.ProcessAsync(directory, searchPattern))
        {
            logger.LogInformation("Completed processing '{id}'. Succeeded: '{succeeded}'.", result.DocumentId, result.Succeeded);
        }
    }
}
