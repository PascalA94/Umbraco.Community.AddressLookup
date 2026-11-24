using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.Providers;

/// <summary>
/// A lookup backend. Implementations are singletons over <c>IHttpClientFactory</c> and <c>IOptionsMonitor</c> (pitfall
/// P1/P2) and map every provider failure to <see cref="AddressLookupUnavailableException"/>.
/// </summary>
public interface IAddressLookupProvider
{
    /// <summary><c>loqate</c>, <c>idealPostcodes</c> or <c>stub</c>.</summary>
    string Alias { get; }

    Task<IReadOnlyList<AddressSuggestion>> FindAsync(AddressQuery query, CancellationToken ct);

    /// <summary>The address for a suggestion id, or null when the provider does not know it.</summary>
    Task<AddressValue?> RetrieveAsync(string id, CancellationToken ct);
}
