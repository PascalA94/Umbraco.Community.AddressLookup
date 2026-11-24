using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.Providers.Loqate;

/// <summary>
/// Loqate Capture Interactive Find v1.20 and Retrieve v1.30. Every call is a POST with a form body, so the key and the
/// visitor's text never appear in a request URI that <c>IHttpClientFactory</c> logs (design 5.1).
/// </summary>
public sealed class LoqateProvider(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<AddressLookupOptions> options,
    TimeProvider time,
    ILogger<LoqateProvider> logger) : IAddressLookupProvider
{
    public const string ClientName = "community.addressLookup.loqate";
    public const string BaseAddress = "https://api.addressy.com/";

    private const int MaxText = 200;

    private readonly ProviderHttp _http = new("loqate", logger, time);

    public string Alias => "loqate";

    public async Task<IReadOnlyList<AddressSuggestion>> FindAsync(AddressQuery query, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("Key", Key()),
            new("Text", query.Text),
            new("Countries", string.Join(',', query.Countries)),
            new("Limit", query.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("IsMiddleware", "true"),
        };
        if (!string.IsNullOrEmpty(query.Container))
        {
            form.Add(new("Container", query.Container));
        }

        using var reply = await PostAsync("Capture/Interactive/Find/v1.20/json6.ws", form, ct);
        var results = new List<AddressSuggestion>();
        var seen = 0;
        foreach (var item in Items(reply.Document))
        {
            seen++;
            ThrowIfError(item);
            if (string.IsNullOrEmpty(Str(item, "Id")) || string.IsNullOrEmpty(Str(item, "Text")))
            {
                continue;
            }

            // Anything that is not a final address drills in with another Find, so an unknown type is safe.
            var type = Str(item, "Type");
            results.Add(new AddressSuggestion(Str(item, "Id") ?? string.Empty, Str(item, "Text") ?? string.Empty, Str(item, "Description"), type != "Address"));
        }

        // Items that are all unreadable mean the answer is not the shape we know (for example a changed error item): fail
        // closed rather than show blank options.
        if (seen > 0 && results.Count == 0)
        {
            throw _http.Fail("BAD-RESPONSE");
        }

        return [.. results.Take(Math.Max(0, query.Limit))];
    }

    public async Task<AddressValue?> RetrieveAsync(string id, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("Key", Key()), new("Id", id) };

        using var reply = await PostAsync("Capture/Interactive/Retrieve/v1.30/json6.ws", form, ct);
        var first = Items(reply.Document).FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        ThrowIfError(first);
        var lines = new[] { "Company", "Line1", "Line2", "Line3" }
            .Select(n => Str(first, n)).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!).ToList();

        return new AddressValue(
            lines, Str(first, "City") ?? string.Empty, Str(first, "Province"), Str(first, "PostalCode") ?? string.Empty,
            Str(first, "CountryIso2") ?? string.Empty, Str(first, "CountryName"), Alias, id, "lookup").Normalise();
    }

    private string Key() => options.CurrentValue.Loqate.ApiKey ?? throw _http.Fail("NO-KEY");

    private async Task<JsonReply> PostAsync(string path, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        var client = httpClientFactory.CreateClient(ClientName);
        return await _http.SendJsonAsync(client, request, options.CurrentValue.TimeoutSeconds, ct);
    }

    private static IEnumerable<JsonElement> Items(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object
        && doc.RootElement.TryGetProperty("Items", out var items)
        && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    // The error-item shape is unverified against live Loqate (spec 0): handle the documented {"Error":"2",...} form and
    // a numeric Error. Only the code is kept; Description, Cause and Resolution can echo the key.
    private void ThrowIfError(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("Error", out var error))
        {
            var raw = error.ValueKind switch
            {
                JsonValueKind.String => error.GetString(),
                JsonValueKind.Number => error.GetRawText(),
                _ => null,
            };
            throw _http.Fail(ProviderHttp.SafeCode(raw));
        }
    }

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
