using System.Net;

namespace Exchange.Api.Tests.Rates;

/// <summary>A fake endpoint: answers every request with <paramref name="answer"/> and records the requests.</summary>
internal sealed class StubHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHttp Serving(byte[] body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) });

    public static StubHttp Throwing(Exception error) => new(_ => throw error);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(answer(request));
    }
}
