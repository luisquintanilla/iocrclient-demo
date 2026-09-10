using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.Documents;

namespace DemoOcr;

public static partial class DocumentExtractionDemoExtensions
{
    public static async IAsyncEnumerable<DocumentExtractionPageResult> StreamAsUpdates(
        Func<CancellationToken, Task<DocumentExtractionResult>> extract,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        DocumentExtractionResult result = await extract(cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < result.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentPage page = result.Pages[index];
            bool terminal = index == result.Pages.Count - 1;
            yield return new DocumentExtractionPageResult(page)
            {
                PagesProcessed = index + 1,
                TotalPages = result.Pages.Count,
                Usage = terminal ? result.Usage : null,
                RawRepresentation = terminal ? result.RawRepresentation : null,
                AdditionalProperties = terminal ? result.AdditionalProperties : null,
            };
        }
    }

    public static string? GetModelId(this DocumentExtractionResult result)
        => result.AdditionalProperties?.TryGetValue("modelId", out object? modelId) == true
            ? modelId as string
            : null;

    public static bool GetIncludeImages(this DocumentExtractionOptions? options)
        => options?.AdditionalProperties?.TryGetValue("includeImages", out object? includeImages) == true
            && includeImages is true;

    public static DocumentNodeId CreateNodeId(string provider, int pageNumber, string kind, int index)
        => new($"{provider}:page-{pageNumber}:{kind}-{index}");

    public static DocumentText CreateTextNode(
        string provider,
        int pageNumber,
        int index,
        string text,
        DocumentTextRole role = DocumentTextRole.Paragraph,
        int? level = null,
        string? language = null)
        => new(
            CreateNodeId(provider, pageNumber, "text", index),
            text,
            role,
            level,
            language,
            pageReferences: [new(pageNumber)]);

    public static Document CreatePageDocument(
        string provider,
        int pageNumber,
        IReadOnlyList<DocumentNode> children)
        => new(
        [
            new DocumentContainer(
                CreateNodeId(provider, pageNumber, "section", 0),
                DocumentContainerRole.Section,
                children,
                pageReferences: [new(pageNumber)]),
        ]);

    public static string ProjectProviderMarkdown(string markdown)
    {
        string normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);
        var output = new List<string>();

        foreach (string sourceLine in normalized.Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || MarkdownTableSeparator().IsMatch(line))
            {
                continue;
            }

            line = MarkdownImage().Replace(line, "$1");
            line = MarkdownLink().Replace(line, "$1");
            line = MarkdownPrefix().Replace(line, string.Empty);
            IReadOnlyList<string> columns = SplitMarkdownColumns(line);
            if (columns.Count > 1)
            {
                line = string.Join('\t', columns);
            }
            else
            {
                line = line.Replace(@"\|", "|", StringComparison.Ordinal);
            }

            line = MarkdownStrongAsterisk().Replace(line, "$1");
            line = MarkdownStrongUnderscore().Replace(line, "$1");
            line = MarkdownEmphasisAsterisk().Replace(line, "$1");
            line = MarkdownEmphasisUnderscore().Replace(line, "$1");
            line = MarkdownStrikethrough().Replace(line, "$1");
            line = MarkdownInlineCode().Replace(line, "$1").Trim();
            if (line.Length > 0)
            {
                output.Add(line);
            }
        }

        return string.Join('\n', output);
    }

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)*\|?\s*$")]
    private static partial Regex MarkdownTableSeparator();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]+\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^\s*(?:#{1,6}\s+|[-+*]\s+|>\s+|\d+\.\s+)")]
    private static partial Regex MarkdownPrefix();

    [GeneratedRegex(@"\*\*([^*\n]+)\*\*")]
    private static partial Regex MarkdownStrongAsterisk();

    [GeneratedRegex(@"__([^_\n]+)__")]
    private static partial Regex MarkdownStrongUnderscore();

    [GeneratedRegex(@"(?<!\*)\*([^*\n]+)\*(?!\*)")]
    private static partial Regex MarkdownEmphasisAsterisk();

    [GeneratedRegex(@"(?<!\w)_([^_\n]+)_(?!\w)")]
    private static partial Regex MarkdownEmphasisUnderscore();

    [GeneratedRegex(@"~~([^~\n]+)~~")]
    private static partial Regex MarkdownStrikethrough();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex MarkdownInlineCode();

    private static IReadOnlyList<string> SplitMarkdownColumns(string line)
    {
        var columns = new List<string>();
        var current = new System.Text.StringBuilder();
        bool foundDelimiter = false;
        for (int index = 0; index < line.Length; index++)
        {
            if (line[index] == '\\' && index + 1 < line.Length && line[index + 1] == '|')
            {
                current.Append('|');
                index++;
            }
            else if (line[index] == '|')
            {
                columns.Add(current.ToString().Trim());
                current.Clear();
                foundDelimiter = true;
            }
            else
            {
                current.Append(line[index]);
            }
        }

        columns.Add(current.ToString().Trim());
        if (!foundDelimiter)
        {
            return columns;
        }

        if (columns.Count > 0 && columns[0].Length == 0)
        {
            columns.RemoveAt(0);
        }
        if (columns.Count > 0 && columns[^1].Length == 0)
        {
            columns.RemoveAt(columns.Count - 1);
        }
        return columns;
    }
}
