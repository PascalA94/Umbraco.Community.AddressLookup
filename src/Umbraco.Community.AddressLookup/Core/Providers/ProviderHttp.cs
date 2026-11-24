using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.AddressLookup.Providers;

/// <summary>
/// The call plumbing every provider shares: the hard timeout, a bounded read, and the failure mapping that guarantees
/// nothing but the provider alias and a code ever leaves a provider (design 4.2, 5.1).
/// </summary>
internal sealed class ProviderHttp(string alias, ILogger logger, TimeProvider time)
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);
    private const int MaxTrackedCodes = 64;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastLogged = new();

    /// <summary>Redirects are never followed: a hostile or misconfigured redirect must not move the key to another host.</summary>
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false };

    /// <summary>A code safe to log and keep: a short run of digits or hyphens from a provider, else <c>UNKNOWN</c>.</summary>
    public static string SafeCode(string? raw) =>
        raw is { Length: > 0 and <= 6 } && raw.All(c => char.IsAsciiDigit(c) || c == '-') ? raw : "UNKNOWN";

    /// <summary>Logs one throttled Warning (provider and code only) and returns the exception to throw.</summary>
    public AddressLookupUnavailableException Fail(string code)
    {
        var now = time.GetUtcNow();
        if (_lastLogged.Count >= MaxTrackedCodes)
        {
            _lastLogged.Clear();
        }

        var due = true;
        _lastLogged.AddOrUpdate(
            code,
            now,
            (_, last) =>
            {
                due = now - last >= LogInterval;
                return due ? now : last;
            });

        if (due)
        {
            logger.LogWarning("Address lookup provider {Provider} failed with code {Code}", alias, code);
        }

        return new AddressLookupUnavailableException(alias, code);
    }

    /// <summary>
    /// Sends the request under the timeout and returns the parsed JSON body. A non-2xx answer is an
    /// <c>HTTP-nnn</c> failure unless <paramref name="keepErrorBody"/> is set and the body parses, for providers that
    /// put their error code in the body.
    /// </summary>
    public async Task<JsonReply> SendJsonAsync(
        HttpClient client, HttpRequestMessage request, int timeoutSeconds, CancellationToken ct, bool keepErrorBody = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode && !keepErrorBody)
            {
                throw Fail($"HTTP-{status}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw Fail("TOO-LARGE");
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            try
            {
                return new JsonReply(status, await JsonDocument.ParseAsync(buffer, cancellationToken: linked.Token));
            }
            catch (JsonException)
            {
                throw Fail(response.IsSuccessStatusCode ? "BAD-RESPONSE" : $"HTTP-{status}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Fail("TIMEOUT");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // A connection dropped mid-body is an IOException, not an HttpRequestException.
            // Not chained on purpose: the exception can carry the request URI.
            throw Fail("NETWORK");
        }
    }
}

/// <summary>A parsed provider answer. Dispose to release the document.</summary>
internal sealed record JsonReply(int Status, JsonDocument Document) : IDisposable
{
    public void Dispose() => Document.Dispose();
}
