using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.VectorData;

namespace hero.Web.Services;

public sealed class IngestedChunk : IngestionChunkVectorRecord
{
    public const int VectorDimensions = 1536;
    public const string VectorDistanceFunction = DistanceFunction.CosineDistance;
    public const string CollectionName = "data-hero-chunks";

    [VectorStoreVector(VectorDimensions, DistanceFunction = VectorDistanceFunction)]
    public override AIContent? Embedding => Content;
}
