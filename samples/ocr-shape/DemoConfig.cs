using Microsoft.Extensions.Configuration;

namespace DemoOcr;

/// <summary>
/// Central credential/config lookup for every demo sample and the hero app. Reads from
/// <c>dotnet user-secrets</c> (UserSecretsId <c>iocrclient-demo</c>) first, then environment
/// variables as a fallback — so endpoints and account names are never committed. This works for
/// file-based samples (<c>dotnet run NN-*.cs</c>) too: no <c>.csproj</c> is required to read
/// user-secrets by id. Set values with, for example:
/// <code>
///   dotnet user-secrets set "OCR:OpenAIEndpoint" "https://your-resource.openai.azure.com/" --id iocrclient-demo
/// </code>
/// Keys used across the demo: <c>OCR:OpenAIEndpoint</c>, <c>OCR:VisionDeployment</c>,
/// <c>OCR:EmbedDeployment</c>, <c>OCR:FoundryEndpoint</c>, <c>OCR:MistralModel</c>,
/// <c>OCR:DocIntelEndpoint</c>, <c>OCR:ContentUnderstandingEndpoint</c>.
/// </summary>
public static class DemoConfig
{
    /// <summary>The shared configuration root (user-secrets, then environment variables).</summary>
    public static IConfiguration Config { get; } = new ConfigurationBuilder()
        .AddUserSecrets("iocrclient-demo")
        .AddEnvironmentVariables()
        .Build();

    /// <summary>Returns the value for <paramref name="key"/> or throws with a copy-paste fix.</summary>
    public static string Require(string key) =>
        Config[key] ?? throw new InvalidOperationException(
            $"Missing config '{key}'. Set it with: " +
            $"dotnet user-secrets set \"{key}\" <value> --id iocrclient-demo");

    /// <summary>Returns the value for <paramref name="key"/> or <paramref name="fallback"/>.</summary>
    public static string Get(string key, string fallback) => Config[key] ?? fallback;
}
