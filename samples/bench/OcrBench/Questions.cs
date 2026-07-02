using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OcrBench;

/// <summary>One authored question with a hand-written reference answer and the short facts a faithful
/// extraction should surface. No gold accuracy is claimed (see questions.json).</summary>
public sealed class BenchQuestion
{
    [JsonPropertyName("question")] public string Question { get; set; } = "";
    [JsonPropertyName("reference")] public string Reference { get; set; } = "";
    [JsonPropertyName("knownFacts")] public string[] KnownFacts { get; set; } = [];
}

public sealed class BenchQuestionSet
{
    [JsonPropertyName("document")] public string Document { get; set; } = "";
    [JsonPropertyName("questions")] public List<BenchQuestion> Questions { get; set; } = [];

    /// <summary>Every distinct known fact across the set — the corpus for KnownFactCoverage.</summary>
    public IReadOnlyList<string> AllKnownFacts() =>
        Questions.SelectMany(q => q.KnownFacts).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static BenchQuestionSet Load(string path)
    {
        // File-based apps disable reflection-based JSON by default; opt in explicitly with a resolver.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        using FileStream fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<BenchQuestionSet>(fs, options)
            ?? throw new InvalidOperationException($"Could not parse {path}.");
    }

    /// <summary>Resolves questions.json next to the OcrBench assembly (CopyToOutputDirectory).</summary>
    public static BenchQuestionSet LoadDefault()
    {
        string dir = Path.GetDirectoryName(typeof(BenchQuestionSet).Assembly.Location) ?? ".";
        return Load(Path.Combine(dir, "questions.json"));
    }
}
