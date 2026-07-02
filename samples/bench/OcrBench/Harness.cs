using System.Diagnostics;
using CommunityToolkit.VectorData.InMemory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using UglyToad.PdfPig;

namespace OcrBench;

/// <summary>The structural + textual result of running ONE extractor over the document.</summary>
public sealed record ExtractionOutcome(
    string Provider, string Text, int PageCount, int TableCount, int ImageCount, long ElapsedMs);

/// <summary>
/// The shared harness: run an extractor (native PdfPig baseline OR any real <see cref="IOcrClient"/>),
/// then chunk / retrieve / answer with the same downstream pipeline so the ONLY variable is the
/// extraction step. Deliberately minimal (lexical retrieval, window chunking) — the OCR seam is what we
/// are measuring, not the retriever.
/// </summary>
public static class Harness
{
    /// <summary>Naive baseline: native PdfPig text layer only. No tables, no figures, no network.</summary>
    public static ExtractionOutcome ExtractNative(string pdfPath)
    {
        var sw = Stopwatch.StartNew();
        using PdfDocument pdf = PdfDocument.Open(pdfPath);
        var pages = new List<string>();
        foreach (UglyToad.PdfPig.Content.Page page in pdf.GetPages())
        {
            pages.Add(page.Text);
        }
        sw.Stop();
        return new ExtractionOutcome("pdfpig-native", string.Join("\n\n", pages), pages.Count, 0, 0, sw.ElapsedMilliseconds);
    }

    /// <summary>Any real IOcrClient. Requests images so TablesDetected/ImagesDetected are meaningful.</summary>
    public static async Task<ExtractionOutcome> ExtractWithOcrAsync(
        string provider, IOcrClient ocr, string pdfPath, string mediaType, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using FileStream doc = File.OpenRead(pdfPath);
        OcrResult result = await ocr.ExtractAsync(doc, mediaType, new OcrOptions { IncludeImages = true }, progress: null, ct);
        sw.Stop();

        string text = string.Join("\n\n", result.Pages.Select(p => p.Markdown));
        int tables = result.Pages.Sum(p => p.Tables.Count);
        int images = result.Pages.Sum(p => p.Images.Count);
        return new ExtractionOutcome(provider, text, result.Pages.Count, tables, images, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// The middle STRATEGY rung (CommunityToolkit/AI #14 "reader owns WHEN"): native PdfPig text first,
    /// and fall back to the injected <see cref="IOcrClient"/> over the whole document only when the native
    /// layer yields no usable text (a scanned / image-only PDF). On a born-digital doc this spends ZERO
    /// OCR calls (native suffices); on a scanned doc it falls back and OCR recovers the text the native
    /// layer can't. That is the honest strategy dimension — not just the two extremes.
    /// </summary>
    public static async Task<ExtractionOutcome> ExtractWithPdfPigFallbackAsync(
        IOcrClient ocr, string pdfPath, string mediaType, int minNativeChars = 64, CancellationToken ct = default)
    {
        ExtractionOutcome native = ExtractNative(pdfPath);
        int nativeChars = native.Text.Count(c => !char.IsWhiteSpace(c));
        if (nativeChars >= minNativeChars)
            return native with { Provider = "pdfpig+ocr-fallback" }; // native sufficed: 0 OCR calls

        ExtractionOutcome viaOcr = await ExtractWithOcrAsync("pdfpig+ocr-fallback", ocr, pdfPath, mediaType, ct);
        return viaOcr; // scanned: fell back to OCR
    }

    /// <summary>Split text into overlapping word windows — a stand-in for a real chunker + vector store.</summary>
    public static List<string> Chunk(string text, int windowWords = 120, int stride = 90)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        for (int i = 0; i < words.Length; i += stride)
        {
            chunks.Add(string.Join(' ', words.Skip(i).Take(windowWords)));
            if (i + windowWords >= words.Length)
            {
                break;
            }
        }
        return chunks.Count == 0 ? [text] : chunks;
    }

    /// <summary>Lexical top-k retrieval over the chunks for a question (offline fallback retriever).</summary>
    public static List<string> Retrieve(string question, IReadOnlyList<string> chunks, int k = 4)
    {
        string[] terms = question.ToLowerInvariant()
            .Split([' ', '?', ',', '.', ';', ':', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 3).ToArray();

        return chunks
            .Select(c => (c, score: terms.Count(t => c.Contains(t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x => x.c)
            .ToList();
    }

    /// <summary>
    /// REAL top-k retrieval: embed the chunks with the given <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>
    /// into a local Microsoft.Extensions.VectorData store (CommunityToolkit.VectorData.InMemory) and run a
    /// vector similarity search. This is the same retriever held IDENTICAL across every extractor row, so
    /// the only variable stays the extraction step — realistic AND fair. Swap InMemory for SqliteVec and
    /// nothing else changes.
    /// </summary>
    public static async Task<List<string>> RetrieveWithVectorStoreAsync(
        IEmbeddingGenerator<string, Embedding<float>> embedder,
        string question, IReadOnlyList<string> chunks, int k = 4, CancellationToken ct = default)
    {
        using var store = new InMemoryVectorStore(new InMemoryVectorStoreOptions { EmbeddingGenerator = embedder });
        var collection = store.GetCollection<Guid, BenchChunk>("chunks");
        await collection.EnsureCollectionExistsAsync(ct);
        await collection.UpsertAsync(chunks.Select(c => new BenchChunk { Key = Guid.NewGuid(), Text = c }), ct);

        var hits = new List<string>();
        await foreach (VectorSearchResult<BenchChunk> r in collection.SearchAsync(question, k, cancellationToken: ct))
            hits.Add(r.Record.Text);
        return hits;
    }

    /// <summary>Answer a question grounded ONLY on the retrieved chunks.</summary>
    public static async Task<string> AnswerAsync(
        IChatClient chat, string question, IReadOnlyList<string> retrieved, CancellationToken ct = default)
    {
        if (retrieved.Count == 0)
        {
            return "No relevant context was retrieved from the extracted text.";
        }

        string context = string.Join("\n\n", retrieved);
        string prompt =
            "Answer the question using ONLY the context. If the context does not contain the answer, say so.\n\n" +
            $"Context:\n{context}\n\nQuestion: {question}";
        ChatResponse response = await chat.GetResponseAsync(prompt, cancellationToken: ct);
        return response.Text;
    }
}

/// <summary>Local vector-store record for the bench retriever. The [VectorStoreVector] property holds
/// the chunk text; the store's configured IEmbeddingGenerator embeds it at upsert/search time.</summary>
public sealed class BenchChunk
{
    [VectorStoreKey] public Guid Key { get; set; }
    [VectorStoreVector(1536)] public string Text { get; set; } = "";
}
