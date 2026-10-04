using System.Net;
using System.Text;

namespace MachineVoice.Core.Tests;

sealed class StubHttpHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public static HttpResponseMessage Chat(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $$$"""{"choices":[{"index":0,"message":{"role":"assistant","content":{{{System.Text.Json.JsonSerializer.Serialize(content)}}}}}]}""",
            Encoding.UTF8,
            "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((request, body));
        return await respond(request, body, cancellationToken);
    }
}
