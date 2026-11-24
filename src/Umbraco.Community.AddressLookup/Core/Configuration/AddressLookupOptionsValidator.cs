using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Umbraco.Community.AddressLookup.Configuration;

/// <summary>
/// Start-up validation. Failure text names the config key and never a value, so a mistyped secret cannot reach the
/// log (design 3.1, 5.4).
/// </summary>
internal sealed class AddressLookupOptionsValidator(IHostEnvironment environment) : IValidateOptions<AddressLookupOptions>
{
    public ValidateOptionsResult Validate(string? name, AddressLookupOptions options)
    {
        var failures = new List<string>();
        var section = AddressLookupOptions.SectionName;

        if (options.MaxSuggestions is < 1 or > 20)
        {
            failures.Add($"{section}:MaxSuggestions must be between 1 and 20.");
        }

        if (options.TimeoutSeconds is < 1 or > 30)
        {
            failures.Add($"{section}:TimeoutSeconds must be between 1 and 30.");
        }

        if (options.RateLimits.FindPerMinute < 1)
        {
            failures.Add($"{section}:RateLimits:FindPerMinute must be at least 1.");
        }

        if (options.RateLimits.RetrievePerMinute < 1)
        {
            failures.Add($"{section}:RateLimits:RetrievePerMinute must be at least 1.");
        }

        if (options.RateLimits.RetrievePerDay < 1)
        {
            failures.Add($"{section}:RateLimits:RetrievePerDay must be at least 1.");
        }

        foreach (var (key, value) in new (string, int)[]
        {
            ("FindPerMinutePerIp", options.RateLimits.FindPerMinutePerIp),
            ("RetrievePerMinutePerIp", options.RateLimits.RetrievePerMinutePerIp),
            ("FindPerDay", options.RateLimits.FindPerDay),
        })
        {
            if (value < 1)
            {
                failures.Add($"{section}:RateLimits:{key} must be at least 1.");
            }
        }

        if (options.Mode == LookupMode.Capture && options.Provider != ProviderKind.Loqate)
        {
            failures.Add($"{section}:Mode Capture needs {section}:Provider Loqate; Ideal Postcodes goes through the server proxy.");
        }

        switch (options.Provider)
        {
            case ProviderKind.Loqate when options.Mode == LookupMode.Proxy:
                if (string.IsNullOrWhiteSpace(options.Loqate.ApiKey))
                {
                    failures.Add($"{section}:Loqate:ApiKey is required when {section}:Provider is Loqate.");
                }

                break;

            case ProviderKind.Loqate:
                if (string.IsNullOrWhiteSpace(options.Loqate.CaptureKey))
                {
                    failures.Add($"{section}:Loqate:CaptureKey is required when {section}:Mode is Capture.");
                }
                // Trimmed on both sides: the page prints the trimmed Capture key, so a stray newline must not hide a reused server key.
                else if (string.Equals(options.Loqate.CaptureKey.Trim(), options.Loqate.ApiKey?.Trim(), StringComparison.Ordinal))
                {
                    failures.Add("Use a separate, URL-restricted key for Capture mode.");
                }

                break;

            case ProviderKind.IdealPostcodes when string.IsNullOrWhiteSpace(options.IdealPostcodes.ApiKey):
                failures.Add($"{section}:IdealPostcodes:ApiKey is required when {section}:Provider is IdealPostcodes.");
                break;

            case ProviderKind.Stub when !environment.IsDevelopment():
                failures.Add($"{section}:Provider Stub is only allowed in the Development environment.");
                break;
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
