using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Umbraco.Community.AddressLookup.Tests.Support;

/// <summary>Records each request (method, URI, form or text body) and answers from a delegate.</summary>
internal sealed class RecordingHandler(Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    /// <summary>A snapshot, safe to read while a request is still in flight.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public static RecordingHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    public static RecordingHandler Status(HttpStatusCode status) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, body, request.Headers.ToDictionary(h => h.Key, h => string.Join(',', h.Value), StringComparer.OrdinalIgnoreCase));
        _requests.Enqueue(recorded);
        return await respond(recorded, ct);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body, Dictionary<string, string> Headers)
{
    public bool Has(string header) => Headers.ContainsKey(header);

    public IReadOnlyDictionary<string, string> Form =>
        Body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri_Decode(p[0]), p => Uri_Decode(p.Length > 1 ? p[1] : string.Empty));

    private static string Uri_Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}
