using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;

namespace Umbraco.Community.AddressLookup.Tests;

public class OptionsTests
{
    private const string LoqateSentinel = "lq-SENTINEL-KEY"; // gitleaks:allow
    private const string IdealSentinel = "ip-SENTINEL-KEY"; // gitleaks:allow

    private static ValidateOptionsResult Validate(AddressLookupOptions options, string environment = "Development")
    {
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        return new AddressLookupOptionsValidator(host).Validate(null, options);
    }

    private static string Failure(ValidateOptionsResult result)
    {
        Assert.True(result.Failed, "expected validation to fail");
        return string.Join(" | ", result.Failures!);
    }

    [Fact]
    public void MissingLoqateKey_FailsNamingKey()
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate,
            IdealPostcodes = { ApiKey = IdealSentinel },
        };

        var message = Failure(Validate(options));

        Assert.Contains("AddressLookup:Loqate:ApiKey", message);
        Assert.DoesNotContain(IdealSentinel, message);
    }

    [Fact]
    public void MissingIdealPostcodesKey_FailsNamingKey()
    {
        var message = Failure(Validate(new AddressLookupOptions { Provider = ProviderKind.IdealPostcodes }));

        Assert.Contains("AddressLookup:IdealPostcodes:ApiKey", message);
    }

    [Theory]
    [InlineData("K-SENTINEL\n")]
    [InlineData(" K-SENTINEL")]
    [InlineData("K-SENTINEL ")]
    public void CaptureKeyEqualsApiKey_AfterTrim_Fails(string captureKey)
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate,
            Mode = LookupMode.Capture,
            Loqate = { ApiKey = "K-SENTINEL", CaptureKey = captureKey }, // gitleaks:allow
        };

        Assert.Contains("Use a separate, URL-restricted key for Capture mode", Failure(Validate(options)));
    }

    [Fact]
    public void CaptureKeyEqualsApiKey_Fails()
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate,
            Mode = LookupMode.Capture,
            Loqate = { ApiKey = LoqateSentinel, CaptureKey = LoqateSentinel },
        };

        var message = Failure(Validate(options));

        Assert.Contains("Use a separate, URL-restricted key for Capture mode", message);
        Assert.DoesNotContain(LoqateSentinel, message);
    }

    [Fact]
    public void CaptureWithoutCaptureKey_FailsNamingKey()
    {
        var options = new AddressLookupOptions { Provider = ProviderKind.Loqate, Mode = LookupMode.Capture };

        Assert.Contains("AddressLookup:Loqate:CaptureKey", Failure(Validate(options)));
    }

    [Fact]
    public void CaptureWithIdealPostcodes_Fails()
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.IdealPostcodes,
            Mode = LookupMode.Capture,
            IdealPostcodes = { ApiKey = IdealSentinel },
        };

        var message = Failure(Validate(options));

        Assert.Contains("Capture", message);
        Assert.Contains("Loqate", message);
        Assert.DoesNotContain(IdealSentinel, message);
    }

    [Fact]
    public void CaptureWithLoqateAndSeparateKey_Passes()
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate,
            Mode = LookupMode.Capture,
            Loqate = { CaptureKey = "capture-key" },
        };

        Assert.True(Validate(options).Succeeded);
    }

    [Fact]
    public void StubOutsideDevelopment_Fails()
    {
        var options = new AddressLookupOptions { Provider = ProviderKind.Stub };

        Assert.True(Validate(options, "Development").Succeeded);
        Assert.Contains("Stub", Failure(Validate(options, "Production")));
    }

    [Fact]
    public void NoProvider_IsValid_ManualEntryOnly()
    {
        Assert.True(Validate(new AddressLookupOptions(), "Production").Succeeded);
    }

    [Theory]
    [InlineData(0, 5, 30)]
    [InlineData(21, 5, 30)]
    [InlineData(8, 0, 30)]
    [InlineData(8, 31, 30)]
    [InlineData(8, 5, 0)]
    public void Limits_OutOfRange_Fail(int maxSuggestions, int timeoutSeconds, int findPerMinute)
    {
        var options = new AddressLookupOptions
        {
            MaxSuggestions = maxSuggestions,
            TimeoutSeconds = timeoutSeconds,
            RateLimits = { FindPerMinute = findPerMinute },
        };

        Assert.True(Validate(options).Failed);
    }

    [Theory]
    [InlineData("FindPerMinutePerIp")]
    [InlineData("RetrievePerMinutePerIp")]
    [InlineData("FindPerDay")]
    public void NewLimits_BelowOne_FailNamingKey(string key)
    {
        var options = new AddressLookupOptions();
        typeof(RateLimitOptions).GetProperty(key)!.SetValue(options.RateLimits, 0);

        Assert.Contains($"AddressLookup:RateLimits:{key}", Failure(Validate(options)));
    }

    [Fact]
    public void ToString_Redacts()
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate,
            Loqate = { ApiKey = LoqateSentinel, CaptureKey = "cap-SENTINEL" }, // gitleaks:allow
            IdealPostcodes = { ApiKey = IdealSentinel },
        };

        foreach (var text in new[] { options.ToString(), options.Loqate.ToString(), options.IdealPostcodes.ToString() })
        {
            Assert.DoesNotContain("SENTINEL", text);
            Assert.Contains("***", text);
        }
    }

    [Fact]
    public void Secrets_AreNotSerialised()
    {
        var options = new AddressLookupOptions
        {
            Loqate = { ApiKey = LoqateSentinel },
            IdealPostcodes = { ApiKey = IdealSentinel },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(options);

        Assert.DoesNotContain("SENTINEL", json);
    }
}
