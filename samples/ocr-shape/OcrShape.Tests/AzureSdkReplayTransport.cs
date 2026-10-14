using Azure;
using Azure.Core;
using Azure.Core.Pipeline;

namespace DemoOcr.Tests;

internal sealed class AzureSdkReplayTransport(params ReplayResponse[] responses)
    : HttpPipelineTransport
{
    private static readonly HttpClientTransport s_requestFactory = new();
    private readonly Queue<ReplayResponse> _responses = new(responses);

    public List<(RequestMethod Method, Uri Uri)> Requests { get; } = [];

    public override Request CreateRequest() => s_requestFactory.CreateRequest();

    public override void Process(HttpMessage message)
    {
        Requests.Add((message.Request.Method, message.Request.Uri.ToUri()));
        message.Response = CreateResponse(_responses.Dequeue());
    }

    public override ValueTask ProcessAsync(HttpMessage message)
    {
        Process(message);
        return ValueTask.CompletedTask;
    }

    private static Response CreateResponse(ReplayResponse response)
        => new ReplayPipelineResponse(response.Status, response.Headers, response.Content);
}

internal sealed record ReplayResponse(
    int Status,
    byte[] Content,
    IReadOnlyDictionary<string, string> Headers)
{
    public static ReplayResponse Accepted(string operationLocation)
        => new(
            202,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operation-location"] = operationLocation,
                ["retry-after"] = "0",
            });

    public static ReplayResponse Json(byte[] content)
        => new(
            200,
            content,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = "application/json",
            });
}

internal sealed class ReplayPipelineResponse(
    int status,
    IReadOnlyDictionary<string, string> headers,
    byte[] content) : Response
{
    private Stream? _contentStream = new MemoryStream(content, writable: false);

    public override int Status => status;

    public override string ReasonPhrase => status is >= 200 and < 300 ? "OK" : "Error";

    public override Stream? ContentStream
    {
        get => _contentStream;
        set => _contentStream = value;
    }

    public override string ClientRequestId { get; set; } = string.Empty;

    public override void Dispose()
    {
    }

    protected override bool ContainsHeader(string name) => headers.ContainsKey(name);

    protected override IEnumerable<HttpHeader> EnumerateHeaders()
        => headers.Select(header => new HttpHeader(header.Key, header.Value));

    protected override bool TryGetHeader(string name, out string value)
    {
        if (headers.TryGetValue(name, out string? header))
        {
            value = header;
            return true;
        }

        value = null!;
        return false;
    }

    protected override bool TryGetHeaderValues(
        string name,
        out IEnumerable<string> values)
    {
        bool found = headers.TryGetValue(name, out string? value);
        values = found ? [value!] : null!;
        return found;
    }
}
