using System.Text.Json.Serialization;

namespace Umbraco.Community.AddressLookup.Configuration;

/// <summary>The <c>AddressLookup</c> configuration section.</summary>
public sealed class AddressLookupOptions
{
    public const string SectionName = "AddressLookup";

    public ProviderKind? Provider { get; set; }

    public LookupMode Mode { get; set; } = LookupMode.Proxy;

    public int MaxSuggestions { get; set; } = 8;

    public int TimeoutSeconds { get; set; } = 5;

    public RateLimitOptions RateLimits { get; set; } = new();

    public LoqateOptions Loqate { get; set; } = new();

    public IdealPostcodesOptions IdealPostcodes { get; set; } = new();

    public override string ToString() =>
        $"AddressLookup {{ Provider = {Provider?.ToString() ?? "(none)"}, Mode = {Mode}, Loqate = {Loqate}, IdealPostcodes = {IdealPostcodes} }}";
}

public sealed class RateLimitOptions
{
    public int FindPerMinute { get; set; } = 30;

    public int RetrievePerMinute { get; set; } = 6;

    public int RetrievePerDay { get; set; } = 1000;

    /// <summary>
    /// Per client IP, across all tickets. A ticket is free (one page view), so a per-ticket limit alone can be reset by
    /// fetching a new one. Behind a CDN or proxy the host must configure forwarded headers for this to see real clients.
    /// </summary>
    public int FindPerMinutePerIp { get; set; } = 60;

    public int RetrievePerMinutePerIp { get; set; } = 12;

    /// <summary>Global cap on Finds, so heavy autocomplete cannot get the provider key throttled for everyone.</summary>
    public int FindPerDay { get; set; } = 20000;
}

public sealed class LoqateOptions
{
    /// <summary>Server key. Never rendered, logged or serialised.</summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    /// <summary>Browser key for Capture mode. A different, URL-restricted key.</summary>
    [JsonIgnore]
    public string? CaptureKey { get; set; }

    public string CaptureScriptVersion { get; set; } = "4.05";

    public override string ToString() => $"Loqate {{ ApiKey = ***, CaptureKey = ***, CaptureScriptVersion = {CaptureScriptVersion} }}";
}

public sealed class IdealPostcodesOptions
{
    /// <summary>Server key. Never rendered, logged or serialised.</summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    public override string ToString() => "IdealPostcodes { ApiKey = *** }";
}
