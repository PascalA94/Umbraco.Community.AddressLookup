using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.Providers.IdealPostcodes;

/// <summary>
/// Ideal Postcodes address autocomplete and the GB resolve. These are GET calls, so the visitor's query is in the URI and
/// the composer silences the <c>HttpClient</c> log categories for this client (design 5.1). The key is sent in a header. The
/// <c>IDPC-Source-IP</c> header is never sent, because it would share visitor IPs.
/// </summary>
public sealed class IdealPostcodesProvider(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<AddressLookupOptions> options,
    TimeProvider time,
    ILogger<IdealPostcodesProvider> logger) : IAddressLookupProvider
{
    public const string ClientName = "community.addressLookup.idealPostcodes";
    public const string BaseAddress = "https://api.ideal-postcodes.co.uk/";

    /// <summary>Ideal Postcodes' "address not found" body code: an empty answer, not an outage.</summary>
    private const string NotFoundCode = "4048";

    private const int MaxText = 200;

    private readonly ProviderHttp _http = new("idealPostcodes", logger, time);

    public string Alias => "idealPostcodes";

    public async Task<IReadOnlyList<AddressSuggestion>> FindAsync(AddressQuery query, CancellationToken ct)
    {
        var uri = $"v1/autocomplete/addresses?query={Uri.EscapeDataString(query.Text)}" +
                  $"&limit={query.Limit.ToString(CultureInfo.InvariantCulture)}";

        using var reply = await GetAsync(uri, ct);
        if (CodeOf(reply) == NotFoundCode)
        {
            return [];
        }

        var results = new List<AddressSuggestion>();
        if (Result(reply) is { ValueKind: JsonValueKind.Object } result
            && result.TryGetProperty("hits", out var hits)
            && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var id = Str(hit, "id");
                var suggestion = Str(hit, "suggestion");
                if (id is null || suggestion is null)
                {
                    continue;
                }

                var comma = suggestion.IndexOf(',');
                results.Add(comma < 0
                    ? new AddressSuggestion(id, suggestion, null, false)
                    : new AddressSuggestion(id, suggestion[..comma], suggestion[(comma + 1)..].Trim(), false));
            }
        }

        return [.. results.Take(Math.Max(0, query.Limit))];
    }

    public async Task<AddressValue?> RetrieveAsync(string id, CancellationToken ct)
    {
        var uri = $"v1/autocomplete/addresses/{Uri.EscapeDataString(id)}/gbr";

        using var reply = await GetAsync(uri, ct);
        if (CodeOf(reply) == NotFoundCode || Result(reply) is not { ValueKind: JsonValueKind.Object } result)
        {
            return null;
        }

        var lines = new[] { "line_1", "line_2", "line_3" }
            .Select(n => Str(result, n)).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();

        // For commercial premises Ideal Postcodes already puts the organisation in line_1 [unverified, check in Task 14], so
        // add it first only when it is not there already.
        var organisation = Str(result, "organisation_name");
        if (!string.IsNullOrWhiteSpace(organisation)
            && !(lines.Count > 0 && lines[0].StartsWith(organisation, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Insert(0, organisation);
        }

        // The resolve endpoint is GB only (design 4.2), so the country is fixed.
        return new AddressValue(
            lines, Str(result, "post_town") ?? string.Empty, Str(result, "county"), Str(result, "postcode") ?? string.Empty,
            "GB", "United Kingdom", Alias, id, "lookup").Normalise();
    }

    private string Key() => options.CurrentValue.IdealPostcodes.ApiKey ?? throw _http.Fail("NO-KEY");

    private async Task<JsonReply> GetAsync(string relativeUri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUri);

        // The key goes in a header, not the query string, so it is not in the request URI that the HttpClient logging and
        // any APM dependency telemetry record. Header form per the Ideal Postcodes API docs [unverified until Task 14].
        request.Headers.TryAddWithoutValidation("Authorization", $"IDEALPOSTCODES api_key=\"{Key()}\"");
        var client = httpClientFactory.CreateClient(ClientName);
        var reply = await _http.SendJsonAsync(client, request, options.CurrentValue.TimeoutSeconds, ct, keepErrorBody: true);

        // Failures carry a numeric "code" in the body (2000 is success). Only the code is kept; "message" can echo input.
        var code = CodeOf(reply);
        if (code is not null && code != "2000" && code != NotFoundCode)
        {
            reply.Dispose();
            throw _http.Fail(ProviderHttp.SafeCode(code));
        }

        if (reply.Status is < 200 or >= 300 && code != NotFoundCode)
        {
            reply.Dispose();
            throw _http.Fail($"HTTP-{reply.Status}");
        }

        return reply;
    }

    private static string? CodeOf(JsonReply reply) =>
        reply.Document.RootElement.ValueKind == JsonValueKind.Object
        && reply.Document.RootElement.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.Number
            ? code.GetRawText()
            : null;

    private static JsonElement? Result(JsonReply reply) =>
        reply.Document.RootElement.ValueKind == JsonValueKind.Object
        && reply.Document.RootElement.TryGetProperty("result", out var result)
            ? result
            : null;

    private static string? Str(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return text is { Length: > MaxText } ? text[..MaxText] : text;
    }
}
