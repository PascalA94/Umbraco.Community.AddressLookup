namespace Umbraco.Community.AddressLookup.Configuration;

/// <summary>How a visitor's search reaches the provider.</summary>
public enum LookupMode
{
    /// <summary>Through this site's rate-limited server-side proxy. The provider key stays on the server.</summary>
    Proxy,

    /// <summary>Loqate's browser script with a separate, URL-restricted key. No proof of lookup is possible.</summary>
    Capture,
}
