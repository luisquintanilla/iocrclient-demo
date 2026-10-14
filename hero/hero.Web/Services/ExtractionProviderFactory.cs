using Azure.Identity;
using DemoOcr;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DocumentExtraction;

namespace hero.Web.Services;

public static class ExtractionProviderFactory
{
    public static IDocumentExtractionClient Create(IServiceProvider services, IConfiguration configuration)
    {
        string provider = (configuration["OCR:Provider"] ?? "vision").ToLowerInvariant();
        var credential = new DefaultAzureCredential();
        return provider switch
        {
            "vision" => new VisionLlmOcrClient(services.GetRequiredService<IChatClient>(),
                useStructuredOutput: configuration.GetValue<bool>("OCR:VisionStructuredOutput")),
            "document-intelligence" => new AzureDocumentIntelligenceClient(
                new Uri(configuration.Require("OCR:DocIntelEndpoint")), credential,
                configuration["OCR:DocIntelModel"] ?? "prebuilt-layout",
                configuration.GetValue<bool>("OCR:IncludeImages")),
            "content-understanding" => new ContentUnderstandingClient(
                new Uri(configuration.Require("OCR:ContentUnderstandingEndpoint")), credential,
                configuration["OCR:ContentUnderstandingAnalyzer"] ?? "prebuilt-document"),
            "mistral" => new FoundryMistralOcrClient(
                new Uri(configuration.Require("OCR:FoundryEndpoint")), credential,
                configuration["OCR:MistralModel"] ?? "mistral-ocr-4-0",
                configuration.GetValue<bool>("OCR:IncludeImages")),
            "pdfpig" => CreatePdfPig(services, configuration),
            _ => throw new InvalidOperationException($"Unsupported OCR:Provider '{provider}'."),
        };
    }

    public static DocumentExtractionOptions CreateOptions(IConfiguration configuration)
    {
        var options = new DocumentExtractionOptions
        {
            ModelId = configuration["OCR:Model"],
            CaptureRawOutput = configuration.GetValue("OCR:CaptureRawOutput", true),
        };
        if (string.Equals(configuration["OCR:Provider"], "pdfpig", StringComparison.OrdinalIgnoreCase))
        {
            options.AdditionalProperties["layoutMode"] = configuration["OCR:PdfPigLayout"] ?? "native";
            options.AdditionalProperties["layoutConfidenceThreshold"] = configuration.GetValue("OCR:LayoutConfidenceThreshold", .5f);
        }
        return options;
    }

    private static IDocumentExtractionClient CreatePdfPig(IServiceProvider services, IConfiguration configuration)
        => (configuration["OCR:PdfPigLayout"] ?? "native") switch
        {
            "native" => new PdfPigDocumentExtractionClient(),
            "rtdetr" => new PdfPigDocumentExtractionClient(
                rasterizer: new PdfPageRasterizer(dpi: 144),
                layoutDetector: services.GetRequiredService<RtDetrLayoutDetector>()),
            var layout => throw new InvalidOperationException($"Unsupported OCR:PdfPigLayout '{layout}'."),
        };
}
