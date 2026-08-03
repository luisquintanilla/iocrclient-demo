using Azure.AI.OpenAI;
using Azure.Identity;
using DemoOcr;
using hero.Web.Components;
using hero.Web.Services;
using hero.Web.Services.Ingestion;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.VectorData;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true, reloadOnChange: true);

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var openAiEndpoint = builder.Configuration.Require("OCR:OpenAIEndpoint");
var chatDeployment = builder.Configuration.Require("OCR:VisionDeployment");
var embeddingDeployment = builder.Configuration.Require("OCR:EmbedDeployment");

var azureOpenAIClient = new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential());

builder.Services.AddChatClient(azureOpenAIClient.GetChatClient(chatDeployment).AsIChatClient())
    .UseFunctionInvocation()
    .UseOpenTelemetry(configure: c =>
        c.EnableSensitiveData = builder.Environment.IsDevelopment());
builder.Services.AddEmbeddingGenerator(azureOpenAIClient.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator())
    .UseOpenTelemetry(configure: c =>
        c.EnableSensitiveData = builder.Environment.IsDevelopment());
builder.Services.AddSingleton<IDocumentExtractionClient>(sp =>
    new VisionLlmOcrClient(sp.GetRequiredService<IChatClient>()));

var configuredVectorStorePath = builder.Configuration["VectorStore:Path"] ?? "vector-store.db";
var vectorStorePath = Path.IsPathFullyQualified(configuredVectorStorePath)
    ? configuredVectorStorePath
    : Path.Combine(AppContext.BaseDirectory, configuredVectorStorePath);
var vectorStoreConnectionString = $"Data Source={vectorStorePath}";
builder.Services.AddSqliteVectorStore(_ => vectorStoreConnectionString);
builder.Services.AddSingleton(sp => sp.GetRequiredService<VectorStore>().GetIngestionRecordCollection<IngestedChunk>(
    IngestedChunk.CollectionName,
    IngestedChunk.VectorDimensions,
    IngestedChunk.VectorDistanceFunction));
builder.Services.AddSingleton<DataIngestor>();
builder.Services.AddSingleton<SemanticSearch>();
builder.Services.AddKeyedSingleton("ingestion_directory", new DirectoryInfo(Path.Combine(builder.Environment.WebRootPath, "Data")));

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

internal static class ConfigurationExtensions
{
    public static string Require(this IConfiguration configuration, string key) =>
        configuration[key] ?? throw new InvalidOperationException(
            $"Missing config '{key}'. Set it with: dotnet user-secrets set \"{key}\" <value> --id iocrclient-demo");
}
