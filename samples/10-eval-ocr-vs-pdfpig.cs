#:project ocr-shape/OcrShape.csproj
#:project bench/OcrBench/OcrBench.csproj
#pragma warning disable MEAI001, MEDE0001, MEAI002, MEAI003
using Microsoft.Extensions.DocumentExtraction;
// 10-eval-ocr-vs-pdfpig.cs — do OCR engines actually beat "naive" PdfPig? Measure it, apples-to-apples.
//
// The fairest possible contrast: the SAME document content in two encodings —
//   usgs-petroleum-assessment.pdf          born-digital (a real text layer + tables + figures)
//   usgs-petroleum-assessment-scanned.pdf  the SAME pages rasterized to images (no text layer)
// Same words, same questions, same downstream pipeline (chunk -> retrieve -> answer). The ONLY variable
// is the extraction step, so any metric delta is extraction, not document choice. Per-document reporting
// tells the graduation story: born-digital -> native text ties OCR on words but recovers 0 tables/figures;
// scanned -> native returns nothing and OCR is the only path that reads the page at all.
//
// Extractor matrix (each gated on its endpoint, so the sample degrades gracefully):
//   pdfpig-native        — native text layer only (the naive baseline: no tables, no figures)
//   pdfpig+ocr-fallback  — native first; fall back to OCR only when the native layer is empty (the #14 strategy)
//   mistral-ocr          — document-native OCR engine
//   azure-di             — document-native OCR engine (figures via output=figures)
//   vision-llm           — a vision model transcribing the page
//
// Retrieval is REAL and held IDENTICAL across every row: a local Microsoft.Extensions.VectorData store
// (CommunityToolkit.VectorData.InMemory) over real Azure OpenAI embeddings when OCR:EmbedDeployment is
// configured; otherwise it degrades to a lexical retriever offline. Same retriever for all rows keeps it fair.
//
// Two evaluators score each extractor:
//   * OcrExtractionEvaluator (CUSTOM, deterministic) — ExtractionYield, TablesDetected, ImagesDetected,
//     KnownFactCoverage. The honest, reproducible contrast.
//   * F1Evaluator (built-in, NLP) — word-overlap of the RAG answer vs a hand-written reference.
// No gold accuracy is claimed: KnownFactCoverage is reference-free, F1 is only against the small authored
// answer set in bench/OcrBench/questions.json. Costs are bounded: few questions, gpt-4.1-mini, one run.
//
//   az login  (keyless)
//   dotnet user-secrets set OCR:FoundryEndpoint <url> --id iocrclient-demo   (see README; never committed)
//   dotnet user-secrets set OCR:EmbedDeployment text-embedding-3-small --id iocrclient-demo
//   dotnet run 10-eval-ocr-vs-pdfpig.cs           # defaults to the born-digital + scanned USGS twins
using System.Text;
using Azure.AI.OpenAI;
using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.NLP;
using OcrBench;

// Same content, two encodings — the apples-to-apples pair. Override by passing PDF paths as args.
string[] docs = args.Length > 0
    ? args
    : ["data/usgs-petroleum-assessment.pdf", "data/usgs-petroleum-assessment-scanned.pdf"];
const string mediaType = "application/pdf";
var cred = new DefaultAzureCredential();

BenchQuestionSet set = BenchQuestionSet.LoadDefault();
IReadOnlyList<string> knownFacts = set.AllKnownFacts();
Console.WriteLine($"documents: {docs.Length}   questions: {set.Questions.Count}   known facts: {knownFacts.Count}\n");

IChatClient chat = new AzureOpenAIClient(new Uri(Require("OCR:OpenAIEndpoint")), cred)
    .GetChatClient(DemoOcr.DemoConfig.Config["OCR:VisionDeployment"] ?? "gpt-4.1-mini")
    .AsIChatClient();

// Real retriever, held identical across rows, when embeddings are configured; else lexical offline.
IEmbeddingGenerator<string, Embedding<float>>? embedder = Opt("OCR:OpenAIEndpoint") is { } embHost
    ? new AzureOpenAIClient(new Uri(embHost), cred)
        .GetEmbeddingClient(Opt("OCR:EmbedDeployment") ?? "text-embedding-3-small")
        .AsIEmbeddingGenerator()
    : null;
Console.WriteLine(embedder is not null
    ? "retriever : real embeddings + local vector store (CommunityToolkit.VectorData.InMemory)\n"
    : "retriever : lexical (offline fallback — set OCR:EmbedDeployment for the real vector store)\n");

// One OCR client drives the pdfpig+ocr-fallback strategy row (first configured provider wins).
Func<IDocumentExtractionClient>? fallbackClient =
      Opt("OCR:FoundryEndpoint") is { } mf ? () => new FoundryMistralOcrClient(new Uri(mf), cred)
    : Opt("OCR:DocIntelEndpoint") is { } df ? () => new AzureDocumentIntelligenceClient(new Uri(df), cred)
    : Opt("OCR:OpenAIEndpoint") is not null ? () => new VisionLlmOcrClient(chat)
    : null;

var custom = new OcrExtractionEvaluator();
var f1 = new F1Evaluator();
var report = new StringBuilder();
report.AppendLine("# OCR-vs-PdfPig eval — leaderboard");
report.AppendLine();
report.AppendLine($"Same content, two encodings (born-digital + scanned) · {set.Questions.Count} questions · {knownFacts.Count} known facts.");
report.AppendLine("Real vector retrieval held identical across rows. Reference-free structural metrics + NLP F1 vs authored references. No gold accuracy claimed.");
report.AppendLine();

foreach (string pdf in docs)
{
    Console.WriteLine($"================  {Path.GetFileName(pdf)}  ================");
    List<Row> rows = await EvaluateDocument(pdf);
    rows = rows.OrderByDescending(r => r.AvgF1).ThenByDescending(r => r.Tables + r.Images).ToList();
    string table = Leaderboard(rows);
    Console.WriteLine(table);
    string takeaway = Takeaway(rows);
    Console.WriteLine(takeaway + "\n");

    report.AppendLine($"## `{Path.GetFileName(pdf)}`");
    report.AppendLine();
    report.AppendLine(table);
    report.AppendLine();
    report.AppendLine(takeaway);
    report.AppendLine();
}

// Report artifact (Markdown text summary — safe to keep; a single non-deterministic run).
string reportDir = Path.Combine(Directory.GetCurrentDirectory(), "bench", "report");
try
{
    Directory.CreateDirectory(reportDir);
    File.WriteAllText(Path.Combine(reportDir, "leaderboard.md"), report.ToString());
    Console.WriteLine("report -> bench/report/leaderboard.md");
}
catch (Exception ex) { Console.WriteLine($"(report not written: {ex.Message})"); }
return 0;

async Task<List<Row>> EvaluateDocument(string pdf)
{
    var extractors = new List<(string Name, Func<Task<ExtractionOutcome>> Run)>
    {
        ("pdfpig-native", () => Task.FromResult(Harness.ExtractNative(pdf))),
    };
    if (fallbackClient is not null)
        extractors.Add(("pdfpig+ocr-fallback", () => Harness.ExtractWithPdfPigFallbackAsync(fallbackClient(), pdf, mediaType)));
    if (Opt("OCR:FoundryEndpoint") is { } mistral)
        extractors.Add(("mistral-ocr", () => Harness.ExtractWithOcrAsync("mistral-ocr", new FoundryMistralOcrClient(new Uri(mistral), cred), pdf, mediaType)));
    if (Opt("OCR:DocIntelEndpoint") is { } di)
        extractors.Add(("azure-di", () => Harness.ExtractWithOcrAsync("azure-di", new AzureDocumentIntelligenceClient(new Uri(di), cred), pdf, mediaType)));
    if (Opt("OCR:OpenAIEndpoint") is not null)
        extractors.Add(("vision-llm", () => Harness.ExtractWithOcrAsync("vision-llm", new VisionLlmOcrClient(chat), pdf, mediaType)));

    var rows = new List<Row>();
    foreach ((string name, Func<Task<ExtractionOutcome>> run) in extractors)
    {
        Console.WriteLine($"--- {name} ---");
        ExtractionOutcome outcome;
        try { outcome = await run(); }
        catch (Exception ex) { Console.WriteLine($"  extraction failed: {ex.GetType().Name}: {ex.Message}\n"); continue; }

        var structural = new OcrStructuralContext(outcome.Text, outcome.PageCount, outcome.TableCount, outcome.ImageCount, knownFacts);
        EvaluationResult sres = await custom.EvaluateAsync([], new ChatResponse(), additionalContext: [structural]);
        double yield = Num(sres, OcrExtractionEvaluator.YieldMetric);
        double tables = Num(sres, OcrExtractionEvaluator.TablesMetric);
        double images = Num(sres, OcrExtractionEvaluator.ImagesMetric);
        double coverage = Num(sres, OcrExtractionEvaluator.CoverageMetric);

        List<string> chunks = Harness.Chunk(outcome.Text);
        var f1s = new List<double>();
        foreach (BenchQuestion q in set.Questions)
        {
            List<string> hits = embedder is not null
                ? await Harness.RetrieveWithVectorStoreAsync(embedder, q.Question, chunks)
                : Harness.Retrieve(q.Question, chunks);
            string answer = await Harness.AnswerAsync(chat, q.Question, hits);
            var messages = new[] { new ChatMessage(ChatRole.User, q.Question) };
            EvaluationResult fres = await f1.EvaluateAsync(messages, new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
                additionalContext: [new F1EvaluatorContext(q.Reference)]);
            f1s.Add(Num(fres, F1Evaluator.F1MetricName));
        }
        double avgF1 = f1s.Count > 0 ? f1s.Average() : 0d;

        Console.WriteLine($"  pages={outcome.PageCount} tables={tables:0} images={images:0} yield={yield:0} chars, coverage={coverage:P0}, avgF1={avgF1:0.00}, {outcome.ElapsedMs}ms\n");
        rows.Add(new Row(name, outcome.PageCount, (int)tables, (int)images, (int)yield, coverage, avgF1, outcome.ElapsedMs));
    }
    return rows;
}

static string Takeaway(List<Row> rows)
{
    Row? baseline = rows.FirstOrDefault(r => r.Name == "pdfpig-native");
    Row? best = rows.FirstOrDefault(r => r.Tables + r.Images > 0) ?? rows.FirstOrDefault(r => r.Name != "pdfpig-native");
    if (baseline is null) return "Takeaway: configure at least one OCR endpoint to contrast against the pdfpig-native baseline.";

    bool scanned = baseline.Yield < 64; // native recovered essentially nothing => an image-only page
    if (scanned)
        return best is not null
            ? $"Takeaway (scanned): pdfpig-native recovers {baseline.Yield} chars / coverage {baseline.Coverage:P0} — the image-only page has no text layer. " +
              $"{best.Name} recovers {best.Yield} chars, coverage {best.Coverage:P0}, {best.Tables} table(s) + {best.Images} figure(s): OCR is the ONLY path that reads the page."
            : "Takeaway (scanned): pdfpig-native recovers ~nothing from the image-only page; configure an OCR endpoint to see the recovery.";
    return best is not null && best.Tables + best.Images > 0
        ? $"Takeaway (born-digital): pdfpig-native ties on text (coverage {baseline.Coverage:P0}, F1 {baseline.AvgF1:0.00}) at a fraction of the latency ({baseline.Ms}ms), " +
          $"but recovers 0 tables / 0 figures. {best.Name} surfaces {best.Tables} table(s) + {best.Images} figure(s) — the structure the naive text layer cannot see."
        : $"Takeaway (born-digital): pdfpig-native covers {baseline.Coverage:P0} at {baseline.Ms}ms; configure an OCR endpoint to contrast structure recovery.";
}

static string Leaderboard(List<Row> rows)
{
    var sb = new StringBuilder();
    sb.AppendLine("| Extractor | Pages | Tables | Images | Yield (chars) | Coverage | avg F1 | ms |");
    sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
    foreach (Row r in rows)
        sb.AppendLine($"| {r.Name} | {r.Pages} | {r.Tables} | {r.Images} | {r.Yield} | {r.Coverage:P0} | {r.AvgF1:0.00} | {r.Ms} |");
    return sb.ToString();
}

static double Num(EvaluationResult r, string metric) =>
    r.Metrics.TryGetValue(metric, out EvaluationMetric? m) && m is NumericMetric n && n.Value is { } v ? v : 0d;

static string Require(string name) =>
    DemoOcr.DemoConfig.Config[name]
    ?? throw new InvalidOperationException($"Set {name} via user-secrets (--id iocrclient-demo); values are never committed.");

static string? Opt(string name)
{
    string? v = DemoOcr.DemoConfig.Config[name];
    return string.IsNullOrWhiteSpace(v) ? null : v;
}

record Row(string Name, int Pages, int Tables, int Images, int Yield, double Coverage, double AvgF1, long Ms);
