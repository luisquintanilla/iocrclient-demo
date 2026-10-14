using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.AI.OpenAI;
using Azure.Identity;
using CommunityToolkit.VectorData.SqliteVec;
using DemoOcr;
using hero.Web.Services;
using hero.Web.Services.Ingestion;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;
using Microsoft.Extensions.VectorData;
using Microsoft.ML.Tokenizers;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

public static class FullE2E
{
    private const string CandidateVersion = "10.10.0-dev.e2e.cf60fa88.1";
    private static readonly Regex Citation = new(@"<citation id='(?<id>[a-f0-9]{64})'>(?<quote>.*?)</citation>",
        RegexOptions.Singleline | RegexOptions.NonBacktracking);

    public static async Task<int> RunAsync(string[] arguments)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        bool allowLive = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i] == "--allow-live")
            {
                allowLive = true;
                continue;
            }
            if (!arguments[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == arguments.Length)
            {
                throw new ArgumentException("Use --mode offline|live --provider pdfpig|all --output <directory> and optional --allow-live.");
            }
            string key = arguments[i];
            if (key is not ("--mode" or "--provider" or "--output" or "--layout" or "--model" or "--model-sha256"))
            {
                throw new ArgumentException($"Unknown argument '{key}'.");
            }
            options.Add(key, arguments[++i]);
        }

        string mode = options.GetValueOrDefault("--mode", "offline");
        if (mode is not ("offline" or "live"))
        {
            throw new ArgumentException("The mode must be offline or live.");
        }
        if (mode == "live" && !allowLive)
        {
            throw new InvalidOperationException("Live mode requires --allow-live and uses existing configured deployments.");
        }
        string[] providers = options.GetValueOrDefault("--provider", "pdfpig") is "all"
            ? ["pdfpig", "document-intelligence", "content-understanding", "mistral", "vision"]
            : options.GetValueOrDefault("--provider", "pdfpig").Split(',');
        if (providers.Any(provider => provider is not ("pdfpig" or "document-intelligence" or "content-understanding" or "mistral" or "vision")))
        {
            throw new ArgumentException("An unknown extraction provider was requested.");
        }
        if (mode == "offline" && providers.Any(provider => provider != "pdfpig"))
        {
            throw new InvalidOperationException("Offline E2E uses native PdfPig, not fake cloud extraction. Use provider replay tests or explicit live mode.");
        }
        string layout = options.GetValueOrDefault("--layout", "native");
        if (layout is not ("native" or "rtdetr") || (layout == "rtdetr" && !providers.Contains("pdfpig")))
        {
            throw new ArgumentException("Use --layout native|rtdetr; RT-DETR requires the PdfPig provider.");
        }
        string? model = options.GetValueOrDefault("--model");
        string? modelHash = options.GetValueOrDefault("--model-sha256");

        string output = Path.GetFullPath(options.GetValueOrDefault("--output", Path.Combine("samples", "output", "e2e", Guid.NewGuid().ToString("N"))));
        Directory.CreateDirectory(output);
        string fixture = Path.Combine(output, "synthetic-source.pdf");
        CreateFixture(fixture);
        var results = new List<object>();
        bool passed = true;
        foreach (string provider in providers)
        {
            if (provider == "pdfpig" && layout == "rtdetr" &&
                (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(modelHash) || !File.Exists(model)))
            {
                passed = false;
                results.Add(new { provider, status = "blocked", reason = "RT-DETR requires an available compatible model and pinned SHA-256." });
                Console.Error.WriteLine("pdfpig: blocked; RT-DETR model or pinned SHA-256 is unavailable.");
                continue;
            }
            string[] missing = RequiredKeys(provider, mode).Where(key => string.IsNullOrWhiteSpace(DemoConfig.Config[key])).ToArray();
            if (missing.Length > 0)
            {
                passed = false;
                results.Add(new { provider, status = "blocked", reason = "Missing configuration keys", keys = missing });
                Console.Error.WriteLine($"{provider}: blocked; missing {string.Join(", ", missing)}");
                continue;
            }
            try
            {
                results.Add(await RunProviderAsync(provider, mode, fixture, output, layout, model, modelHash));
                Console.WriteLine($"{provider}: passed ({mode}; embedding/chat {(mode == "live" ? "live" : "deterministic test doubles")})");
            }
            catch (Exception exception)
            {
                passed = false;
                int? statusCode = exception switch
                {
                    Azure.RequestFailedException requestFailure => requestFailure.Status,
                    HttpRequestException { StatusCode: { } status } => (int)status,
                    _ => null,
                };
                bool blocked = statusCode is 401 or 403 or 404 || exception is AuthenticationFailedException or CredentialUnavailableException;
                string? reason = exception switch
                {
                    ProofFailureException proofFailure => proofFailure.Message,
                    FileNotFoundException missingFile => $"Missing runtime dependency: {Path.GetFileName(missingFile.FileName)}",
                    _ => null,
                };
                object? authenticationDiagnostics = exception is CredentialUnavailableException or AuthenticationFailedException
                    ? new
                    {
                        innerErrorType = exception.InnerException?.GetType().Name,
                        azureCliMentioned = exception.Message.Contains("AzureCliCredential", StringComparison.OrdinalIgnoreCase),
                        cliMissing = exception.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase),
                        loginRequired = exception.Message.Contains("az login", StringComparison.OrdinalIgnoreCase),
                        timedOut = exception.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase),
                        pythonModuleMissing = exception.Message.Contains("No module named", StringComparison.OrdinalIgnoreCase),
                        pathTooLong = exception.Message.Contains("too long", StringComparison.OrdinalIgnoreCase),
                        identityErrorCodes = Regex.Matches(exception.Message, @"AADSTS\d+").Select(match => match.Value).Distinct().ToArray(),
                    }
                    : null;
                results.Add(new { provider, status = blocked ? "blocked" : "failed", errorType = exception.GetType().Name, statusCode, reason, authenticationDiagnostics });
                Console.Error.WriteLine($"{provider}: {(blocked ? "blocked" : "failed")} ({exception.GetType().Name}). {reason}");
            }
        }

        await File.WriteAllTextAsync(Path.Combine(output, "run.json"), JsonSerializer.Serialize(new
        {
            status = passed ? "passed" : "incomplete",
            mode,
            layout,
            authentication = mode == "live" ? "DefaultAzureCredential" : "offline; no service authentication",
            layoutModelSha256 = layout == "rtdetr" ? modelHash : null,
            candidateVersion = CandidateVersion,
            sourceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture))),
            results,
            claims = new
            {
                liveAi = mode == "live" && passed,
                allFiveProviders = providers.Distinct().Count() == 5 && passed,
                onnxInference = layout == "rtdetr" && providers.Contains("pdfpig") && passed,
                releaseReady = false,
            },
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static async Task<object> RunProviderAsync(string provider, string mode, string fixture, string output,
        string layout, string? model, string? modelHash)
    {
        string[] identities = new[] { typeof(Document), typeof(IDocumentExtractionClient), typeof(IngestionPipeline) }
            .Select(type => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
            .ToArray();
        Check(identities.All(identity => identity.StartsWith(CandidateVersion, StringComparison.Ordinal)),
            "A loaded core/MEDI assembly is not from the exact integrated candidate.");

        var credential = new DefaultAzureCredential();
        AzureOpenAIClient? azure = mode == "live"
            ? new(new Uri(DemoConfig.Require("OCR:OpenAIEndpoint")), credential) : null;
        using IEmbeddingGenerator<string, Embedding<float>>? liveEmbedding = azure?.GetEmbeddingClient(
            DemoConfig.Require("OCR:EmbedDeployment")).AsIEmbeddingGenerator();
        using IEmbeddingGenerator<AIContent, Embedding<float>> embedding = liveEmbedding is null
            ? new DeterministicEmbedding() : new TextEmbeddingAdapter(liveEmbedding);
        using IChatClient? liveChat = azure?.GetChatClient(DemoConfig.Require("OCR:VisionDeployment")).AsIChatClient();
        using RtDetrLayoutDetector? detector = provider == "pdfpig" && layout == "rtdetr"
            ? new RtDetrLayoutDetector(model!, expectedSha256: modelHash!) : null;
        using IDocumentExtractionClient extractor = provider switch
        {
            "pdfpig" => new PdfPigDocumentExtractionClient(
                rasterizer: detector is null ? null : new PdfPageRasterizer(dpi: 144), layoutDetector: detector),
            "document-intelligence" => new AzureDocumentIntelligenceClient(new Uri(DemoConfig.Require("OCR:DocIntelEndpoint")), credential),
            "content-understanding" => new ContentUnderstandingClient(new Uri(DemoConfig.Require("OCR:ContentUnderstandingEndpoint")), credential),
            "mistral" => new FoundryMistralOcrClient(new Uri(DemoConfig.Require("OCR:FoundryEndpoint")), credential),
            "vision" => new VisionLlmOcrClient(liveChat ?? throw new InvalidOperationException("Vision requires a live chat client.")),
            _ => throw new ArgumentException("Unknown provider."),
        };
        var extractionOptions = new DocumentExtractionOptions();
        if (provider == "pdfpig") extractionOptions.AdditionalProperties["layoutMode"] = layout;
        IngestionDocumentReader reader = new DocumentExtractionReader(extractor, extractionOptions);
        using Document document = await ReadAsync();
        await File.WriteAllTextAsync(Path.Combine(output, $"{provider}-document.json"), JsonSerializer.Serialize(new
        {
            fixture = "synthetic public fixture",
            markdown = document.ToMarkdown(),
            sourcePageNumbers = document.SourcePages.Select(page => page.PageNumber),
            sourceOutputStatuses = document.SourceOutputs.Select(source => source.NativeStatus.ToString()),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(document.SourcePages.Select(page => page.PageNumber).ToHashSet().SetEquals([1, 2, 3]),
            "The extracted document did not preserve exactly the three actual source pages, including the blank page.");
        Check(document.ToMarkdown().Contains("ORBIT-742", StringComparison.Ordinal), "The known source fact was not extracted.");
        Check(document.SourceOutputs.Count > 0 && document.SourceOutputs.All(source => source.NativeStatus != DocumentRawContentStatus.Disabled),
            "Default raw capture was unexpectedly disabled.");

        string db = Path.Combine(output, $"{provider}.db");
        Check(!File.Exists(db), "A validation database already exists; use a fresh output directory.");
        var options = new SqliteVectorStoreOptions { EmbeddingGenerator = embedding };
        var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");
        var layoutOverlaps = new Dictionary<string, double>(StringComparer.Ordinal);
        using (var store = new SqliteVectorStore($"Data Source={db};Pooling=false", options))
        {
            var collection = store.GetIngestionRecordCollection<IngestedChunk>(IngestedChunk.CollectionName, IngestedChunk.VectorDimensions);
            await IngestAsync(collection);
            IReadOnlyList<IngestedChunk> first = await RetrieveAsync(collection, 100);
            Check(first.Count > 0, "The real MEDI writer did not persist chunks.");
            Check(first.All(record => record.Key != Guid.Empty) && first.Select(record => record.Key).Distinct().Count() == first.Count,
                "Persisted chunks have empty or colliding vector record keys.");
            Check(first.All(record => !string.IsNullOrEmpty(record.CitationId) && record.SourceHash == (string)document.Metadata["demo.source.sha256"]!),
                "The writer lost durable citation identity or the source hash.");
            if (detector is not null)
            {
                Check(first.Any(record => (JsonSerializer.Deserialize<SourceRegionEvidence[]>(record.SourceRegionsJson)
                    ?? throw new ProofFailureException("Persisted source-region evidence is null."))
                    .Any(region => region.Confidence >= .5 && !string.IsNullOrWhiteSpace(region.ConfidenceKind))),
                    "Actual RT-DETR detection confidence/regions did not survive MEDI and the typed persisted writer.");
                foreach (IngestedChunk record in first)
                {
                    SourceRegionEvidence[] regions = JsonSerializer.Deserialize<SourceRegionEvidence[]>(record.SourceRegionsJson)
                        ?? throw new ProofFailureException("Persisted source-region evidence is null.");
                    double overlap = regions.Where(region => region.Confidence is null)
                        .SelectMany(native => regions.Where(detection => detection.Confidence >= .5 &&
                            detection.PageIdentifier == native.PageIdentifier)
                            .Select(detection => BoxIntersectionOverUnion(native.Polygon, detection.Polygon)))
                        .DefaultIfEmpty(0).Max();
                    Check(overlap >= .5,
                        $"RT-DETR geometry on the non-square source fixture has IoU {overlap:F3}; required >= 0.500 against native source text.");
                    layoutOverlaps[record.CitationId] = overlap;
                }
            }
            await IngestAsync(collection);
            IReadOnlyList<IngestedChunk> second = await RetrieveAsync(collection, 100);
            await File.WriteAllTextAsync(Path.Combine(output, $"{provider}-reingestion.json"), JsonSerializer.Serialize(new
            {
                first = first.Select(record => new { record.Key, record.CitationId, record.DocumentId, record.SourceHash }),
                second = second.Select(record => new { record.Key, record.CitationId, record.DocumentId, record.SourceHash }),
            }, new JsonSerializerOptions { WriteIndented = true }));
            Check(first.Count == second.Count && first.Select(record => record.CitationId).ToHashSet().SetEquals(second.Select(record => record.CitationId)),
                "Reingestion duplicated chunks or changed stable source/content identities.");
        }
        Check(!document.IsDisposed, "MEDI disposed the caller-owned source document.");

        using var reopened = new SqliteVectorStore($"Data Source={db};Pooling=false", options);
        var persisted = reopened.GetIngestionRecordCollection<IngestedChunk>(IngestedChunk.CollectionName, IngestedChunk.VectorDimensions);
        IReadOnlyList<IngestedChunk> hits = await RetrieveAsync(persisted, 4);
        Check(hits.Any(record => (record.Content as TextContent)?.Text.Contains("ORBIT-742", StringComparison.Ordinal) == true),
            "Persisted vector retrieval did not recover the source fact.");
        var registry = new CitationRegistry();
        registry.Register(hits);
        IReadOnlyList<IngestedChunk> backupHits = await RetrieveAsync(persisted, 4, "What is the backup code?");
        registry.Register(backupHits);
        IngestedChunk? backup = backupHits.FirstOrDefault(record =>
            (record.Content as TextContent)?.Text.Contains("MAPLE-915", StringComparison.Ordinal) == true);
        Check(backup is not null && registry.TryResolve(backup.CitationId, "MAPLE-915", out CitationEvidence? backupEvidence)
            && backupEvidence is not null && backupEvidence.Pages.Any(page => page.PageNumber == 2),
            "The independently retrieved backup fact lost its actual second-page attribution.");
        Check(!registry.TryResolve(new string('0', 64), "validation", out _), "An invented citation was accepted.");
        Check(!registry.TryResolve(hits[0].CitationId, "a quote that never appeared in this document", out _), "An unsupported quote was accepted.");
        using IChatClient? offlineChat = liveChat is null ? new DeterministicChat(hits) : null;
        IChatClient chat = liveChat ?? offlineChat ?? throw new InvalidOperationException("No chat client was constructed.");
        string context = string.Join("\n", hits.Select(record =>
            $"<result id='{record.CitationId}'>{System.Security.SecurityElement.Escape((record.Content as TextContent)?.Text ?? "")}</result>"));
        ChatResponse answer = await chat.GetResponseAsync([
            new(ChatRole.System, "Answer only from retrieved evidence. Cite each supporting result as <citation id='exact result id'>exact source quote</citation>. If unsupported, say insufficient evidence. Do not invent page numbers."),
            new(ChatRole.User, $"Question: What is the validation code?\nRetrieved evidence:\n{context}"),
        ]);
        await File.WriteAllTextAsync(Path.Combine(output, $"{provider}-answer.json"), JsonSerializer.Serialize(new
        {
            fixture = "synthetic public fixture",
            text = answer.Text,
            retrievedCitationIds = hits.Select(record => record.CitationId),
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(answer.Text.Contains("ORBIT-742", StringComparison.Ordinal), "The grounded answer omitted the known fact.");
        Match[] citations = Citation.Matches(answer.Text).Cast<Match>().ToArray();
        Check(citations.Length > 0, "The answer has no machine-resolvable citation.");
        foreach (Match citation in citations)
        {
            Check(registry.TryResolve(citation.Groups["id"].Value, citation.Groups["quote"].Value, out CitationEvidence? evidence)
                && evidence is not null && evidence.Pages.Any(page => page.PageNumber == 1),
                "The answer citation was not supported by retrieved evidence from the actual source page.");
        }
        ChatResponse unsupported = await chat.GetResponseAsync([
            new(ChatRole.System, "Use only the supplied evidence. If it cannot answer the question, reply exactly 'Insufficient evidence.' without citations."),
            new(ChatRole.User, $"Question: What dinosaur species was observed?\nRetrieved evidence:\n{context}"),
        ]);
        Check(unsupported.Text.Trim().Equals("Insufficient evidence.", StringComparison.OrdinalIgnoreCase),
            "The no-answer case did not abstain as required.");
        return new
        {
            provider,
            status = "passed",
            mode,
            layout = provider == "pdfpig" ? layout : "provider-native",
            loadedAssemblies = identities,
            pages = document.SourcePages.Count,
            retrievedRecords = hits.Count,
            citations = citations.Length,
            realMediPipeline = true,
            realPersistedVectorStore = true,
            reopened = true,
            reingestion = true,
            borrowedDocumentUsable = true,
            inputKind = "synthetic public fixture",
            onnxInference = detector is not null,
            layoutGeometryMinimumIoU = detector is null ? (double?)null : .5,
            layoutGeometryMeasuredIoU = detector is null ? null : layoutOverlaps,
        };

        async Task<Document> ReadAsync()
        {
            await using FileStream source = File.OpenRead(fixture);
            Document result = await reader.ReadAsync(source, "synthetic-source.pdf", "application/pdf");
            result.Metadata["demo.source.sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture))).ToLowerInvariant();
            return result;
        }

        async Task IngestAsync(VectorStoreCollection<Guid, IngestedChunk> collection)
        {
            using var pipeline = new IngestionPipeline(
                new SectionChunker(new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 128 }),
                new CitationVectorStoreWriter(collection));
            Document processed = await pipeline.ProcessAsync(document);
            Check(ReferenceEquals(processed, document), "The default pipeline replaced or translated the shared Document.");
        }
    }

    private static async Task<IReadOnlyList<IngestedChunk>> RetrieveAsync(VectorStoreCollection<Guid, IngestedChunk> collection, int top,
        string query = "What is the validation code?")
    {
        var hits = new List<IngestedChunk>();
        await foreach (var result in collection.SearchAsync(new TextContent(query), top))
        {
            hits.Add(result.Record);
        }
        return hits;
    }

    private static IEnumerable<string> RequiredKeys(string provider, string mode)
    {
        if (mode == "live")
        {
            yield return "OCR:OpenAIEndpoint";
            yield return "OCR:EmbedDeployment";
            yield return "OCR:VisionDeployment";
        }
        if (provider == "document-intelligence") yield return "OCR:DocIntelEndpoint";
        if (provider == "content-understanding") yield return "OCR:ContentUnderstandingEndpoint";
        if (provider == "mistral") yield return "OCR:FoundryEndpoint";
    }

    private static void CreateFixture(string path)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("The validation code is ORBIT-742.", 14, new PdfPoint(72, 720), font);
        builder.AddPage(PageSize.A4).AddText("The backup code is MAPLE-915.", 14, new PdfPoint(72, 720), font);
        builder.AddPage(PageSize.A4);
        File.WriteAllBytes(path, builder.Build());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new ProofFailureException(message);
    }

    private static double BoxIntersectionOverUnion(double[][] first, double[][] second)
    {
        Check(first.Length >= 3 && second.Length >= 3, "A source region has no valid polygon.");
        double firstLeft = first.Min(point => point[0]), firstRight = first.Max(point => point[0]);
        double firstBottom = first.Min(point => point[1]), firstTop = first.Max(point => point[1]);
        double secondLeft = second.Min(point => point[0]), secondRight = second.Max(point => point[0]);
        double secondBottom = second.Min(point => point[1]), secondTop = second.Max(point => point[1]);
        double intersection = Math.Max(0, Math.Min(firstRight, secondRight) - Math.Max(firstLeft, secondLeft))
            * Math.Max(0, Math.Min(firstTop, secondTop) - Math.Max(firstBottom, secondBottom));
        double union = (firstRight - firstLeft) * (firstTop - firstBottom)
            + (secondRight - secondLeft) * (secondTop - secondBottom) - intersection;
        Check(union > 0, "Source comparison regions have no positive area.");
        return intersection / union;
    }

    private sealed class ProofFailureException(string message) : Exception(message);

    private sealed class DeterministicEmbedding : IEmbeddingGenerator<AIContent, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<AIContent> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            GeneratedEmbeddings<Embedding<float>> result = [];
            foreach (AIContent value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string text = value is TextContent content ? content.Text : throw new NotSupportedException("The deterministic fixture expects text.");
                float[] vector = new float[IngestedChunk.VectorDimensions];
                vector[0] = text.Contains("validation", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                vector[1] = text.Contains("backup", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                vector[2] = 0.01f;
                result.Add(new(vector));
            }
            return Task.FromResult(result);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }

    private sealed class DeterministicChat(IReadOnlyList<IngestedChunk> hits) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prompt = messages.Last().Text ?? "";
            if (!prompt.StartsWith("Question: What is the validation code?", StringComparison.Ordinal))
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Insufficient evidence.")));
            }
            foreach (IngestedChunk hit in hits)
            {
                string text = (hit.Content as TextContent)?.Text ?? "";
                Match fact = Regex.Match(text, @"validation code is (?<code>[A-Z]+-\d+)", RegexOptions.NonBacktracking);
                if (fact.Success)
                {
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        $"The validation code is {fact.Groups["code"].Value}. <citation id='{hit.CitationId}'>{fact.Value}</citation>")));
                }
            }
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Insufficient evidence.")));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
