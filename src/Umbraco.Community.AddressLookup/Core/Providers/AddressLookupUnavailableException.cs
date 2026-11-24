namespace Umbraco.Community.AddressLookup.Providers;

/// <summary>
/// A provider could not answer. Carries the provider alias and an error code only: never provider message text, a key,
/// a query or an address, and never an inner exception that might hold the request (design 4.2, 5.1).
/// </summary>
public sealed class AddressLookupUnavailableException(string provider, string code)
    : Exception($"Address lookup provider '{provider}' is unavailable (code {code}).")
{
    public string Provider { get; } = provider;

    public string Code { get; } = code;
}
