using Microsoft.Extensions.AI;

namespace hero.Web.Services;

public sealed class TextEmbeddingAdapter(IEmbeddingGenerator<string, Embedding<float>> inner)
    : IEmbeddingGenerator<AIContent, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<AIContent> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.GenerateAsync(values.Select(value => value is TextContent text
            ? text.Text
            : throw new NotSupportedException("This embedding deployment accepts text content only.")),
            options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The caller owns the borrowed inner generator.
    }
}
