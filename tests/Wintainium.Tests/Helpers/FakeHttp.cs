using System.Net;
using System.Text;

namespace Wintainium.Tests.Helpers;

/// <summary>
/// Stands in for the network: answers requests from registered handlers and records every
/// request. An unregistered URL answers 404. Tests never reach the real network.
/// </summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

    public FakeHttp On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        routes.Add((r => string.Equals(r.RequestUri?.AbsoluteUri, new Uri(url).AbsoluteUri, StringComparison.Ordinal), respond));
        return this;
    }

    public FakeHttp OnJson(string url, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(url, _ => Json(json, status));

    public FakeHttp OnStatus(string url, HttpStatusCode status) =>
        On(url, _ => new HttpResponseMessage(status) { Content = new StringContent("") });

    public FakeHttp OnThrow(string url, Exception ex) => On(url, _ => throw ex);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/html") };

    public int CountFor(string urlPrefix) =>
        Requests.Count(r => r.RequestUri!.AbsoluteUri.StartsWith(urlPrefix, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        foreach ((Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond) in routes)
        {
            if (match(request))
            {
                return Task.FromResult(respond(request));
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") });
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();
}
