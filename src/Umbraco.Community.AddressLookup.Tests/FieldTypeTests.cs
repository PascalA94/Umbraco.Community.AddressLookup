using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Tests.Support;
using Umbraco.Forms.Core.Models;

namespace Umbraco.Community.AddressLookup.Tests;

public class FieldTypeTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly Field _field = new() { Id = Guid.NewGuid(), Settings = new Dictionary<string, string>() };
    private readonly AddressLookupOptions _options = new() { Provider = ProviderKind.Stub };
    private readonly AddressProofService _proofs;

    public FieldTypeTests()
    {
        _proofs = new AddressProofService(DataProtectionProvider.Create("address-lookup-tests"), _time);
    }

    // Per-field settings live on Field.Settings; the properties on the field type only declare them to Forms.
    private AddressLookupFieldType Create(params (string Key, string Value)[] settings)
    {
        foreach (var (key, value) in settings)
        {
            _field.Settings[key] = value;
        }

        var provider = Substitute.For<IAddressLookupProvider>();
        provider.Alias.Returns("stub");
        var type = new AddressLookupFieldType(new TestOptionsMonitor<AddressLookupOptions>(_options), _proofs, [provider]);
        return type;
    }

    private static AddressValue Picked() => new(
        ["Flat 2", "10 High Street"], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "stub", "stub-a2", "lookup");

    private HttpContext Post(Dictionary<string, string> parts)
    {
        var form = parts.ToDictionary(p => $"{_field.Id}.{p.Key}", p => new StringValues(p.Value));
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(form);
        return context;
    }

    private static Dictionary<string, string> Manual(Action<Dictionary<string, string>>? edit = null)
    {
        var parts = new Dictionary<string, string>
        {
            ["line1"] = "Flat 2",
            ["line2"] = "10 High Street",
            ["town"] = "Leeds",
            ["postcode"] = "ls14ap",
            ["country"] = "GB",
        };
        edit?.Invoke(parts);
        return parts;
    }

    private static Dictionary<string, string> WithProof(string proof, Action<Dictionary<string, string>>? edit = null) =>
        Manual(p =>
        {
            p["postcode"] = "LS1 4AP";
            p["providerId"] = "stub-a2";
            p["proof"] = proof;
            edit?.Invoke(p);
        });

    private IReadOnlyList<string> Validate(AddressLookupFieldType type, Dictionary<string, string> parts, bool mandatory = true)
    {
        _field.Mandatory = mandatory;
        return type.ValidateField(new Form(), _field, [], Post(parts), null!, null!).ToList();
    }

    private AddressValue? Stored(AddressLookupFieldType type, Dictionary<string, string> parts)
    {
        var stored = type.ProcessSubmittedValue(_field, [], Post(parts)).Cast<object>().ToList();
        if (stored.Count == 0)
        {
            return null;
        }

        Assert.True(AddressValue.TryParse((string)stored.Single(), out var value));
        return value;
    }

    [Fact]
    public void Lookup_WithValidProof_StoresSourceLookup()
    {
        var proof = _proofs.Issue(Picked(), _field.Id);
        var value = Stored(Create(), WithProof(proof));

        Assert.NotNull(value);
        Assert.Equal("lookup", value.Source);
        Assert.Equal("stub", value.Provider);
        Assert.Equal("stub-a2", value.ProviderId);
        Assert.Equal("United Kingdom", value.Country);
        Assert.Equal("LS1 4AP", value.Postcode);
    }

    [Fact]
    public void EditedAfterPick_StoredAsManual()
    {
        var proof = _proofs.Issue(Picked(), _field.Id);
        var parts = WithProof(proof, p => p["line2"] = "12 High Street");

        var type = Create();
        var value = Stored(type, parts);

        Assert.NotNull(value);
        Assert.Equal("manual", value.Source);
        Assert.Null(value.Provider);
        Assert.Null(value.ProviderId);
        Assert.Empty(Validate(type, parts));
    }

    [Fact]
    public void ProofForAnotherField_StoredAsManual()
    {
        var proof = _proofs.Issue(Picked(), Guid.NewGuid());

        Assert.Equal("manual", Stored(Create(), WithProof(proof))!.Source);
    }

    [Fact]
    public void TamperedProof_RequireLookup_Error()
    {
        var type = Create(("RequireLookup", "true"));

        var errors = Validate(type, WithProof("tampered"));

        Assert.Equal(["Select an address from the list"], errors);
    }

    [Fact]
    public void RequireLookup_WithValidProof_NoError()
    {
        var type = Create(("RequireLookup", "true"));
        var proof = _proofs.Issue(Picked(), _field.Id);

        Assert.Empty(Validate(type, WithProof(proof)));
    }

    [Fact]
    public void RequireLookup_IgnoredInCaptureMode()
    {
        // Capture results cannot carry a proof (design 4.3), so enforcing it would refuse every submission.
        _options.Mode = LookupMode.Capture;
        var type = Create(("RequireLookup", "true"));

        Assert.Empty(Validate(type, Manual()));
    }

    [Fact]
    public void Mandatory_MissingPostcode_Error() =>
        Assert.Contains("Enter a postcode", Validate(Create(), Manual(p => p.Remove("postcode"))));

    [Fact]
    public void Mandatory_MissingLine1_Error() =>
        Assert.Contains("Enter the first line of the address", Validate(Create(), Manual(p => p.Remove("line1"))));

    [Fact]
    public void Mandatory_MissingTown_Error() =>
        Assert.Contains("Enter a town or city", Validate(Create(), Manual(p => p.Remove("town"))));

    [Fact]
    public void Mandatory_AllEmpty_ListsEveryMissingPart() =>
        Assert.Equal(
            ["Enter the first line of the address", "Enter a town or city", "Enter a postcode"],
            Validate(Create(), []));

    [Fact]
    public void Optional_PartlyFilled_StillNeedsTheRequiredParts() =>
        Assert.Equal(["Enter a postcode"], Validate(Create(), Manual(p => p.Remove("postcode")), mandatory: false));

    [Theory]
    [InlineData("ZZ1")]
    [InlineData("12345")]
    [InlineData("LS1 4A")]
    public void BadGbPostcode_Error(string postcode) =>
        Assert.Equal(["Enter a real postcode"], Validate(Create(), Manual(p => p["postcode"] = postcode)));

    [Fact]
    public void NonGbPostcode_OnlyLengthChecked()
    {
        var type = Create(("Countries", "GB, IE"));
        var parts = Manual(p => { p["country"] = "IE"; p["postcode"] = "D02 AF30"; });

        Assert.Empty(Validate(type, parts));
        Assert.Equal(["Enter a real postcode"], Validate(type, Manual(p => { p["country"] = "IE"; p["postcode"] = "D02 AF30 X1234"; })));
    }

    [Theory]
    [InlineData("FR")]
    [InlineData("zz")]
    [InlineData("GBR")]
    public void CountryNotAllowed_Error(string country) =>
        Assert.Equal(["Choose a country from the list"], Validate(Create(), Manual(p => p["country"] = country)));

    [Fact]
    public void EmptyCountry_DefaultsToFirstAllowed()
    {
        var value = Stored(Create(("Countries", "ie, gb")), Manual(p => p.Remove("country")));

        Assert.Equal("IE", value!.CountryIso2);
        Assert.Equal("Ireland", value.Country);
    }

    [Theory]
    [InlineData("line1", 101)]
    [InlineData("line2", 101)]
    [InlineData("line3", 101)]
    [InlineData("town", 61)]
    [InlineData("county", 61)]
    public void OversizePart_Error(string part, int length) =>
        Assert.Equal(["A part of the address is too long"], Validate(Create(), Manual(p => p[part] = new string('a', length))));

    [Theory]
    [InlineData("line1", "10\u0000 High Street")]
    [InlineData("town", "Le\u0007eds")]
    [InlineData("county", "West‮Yorks")]
    [InlineData("line3", "a\r\nb")]
    public void ControlChars_Error(string part, string value) =>
        Assert.Equal(["Remove any special characters from the address"], Validate(Create(), Manual(p => p[part] = value)));

    [Fact]
    public void Unicode_PartsRoundTrip()
    {
        var type = Create();
        var parts = Manual(p => { p["line2"] = "Rhosneigr"; p["town"] = "Ynys Môn"; p["county"] = "Cymru 🏴"; });
        var stored = (string)type.ProcessSubmittedValue(_field, [], Post(parts)).Cast<object>().Single();

        Assert.Contains("Ynys Môn", stored);
        Assert.True(AddressValue.TryParse(stored, out var value));
        Assert.Equal("Ynys Môn", value!.Town);
        Assert.Equal("Cymru 🏴", value.County);
    }

    [Fact]
    public void Optional_AllEmpty_StoresNothing()
    {
        var type = Create();

        Assert.Empty(Validate(type, new() { ["country"] = "GB" }, mandatory: false));
        Assert.Empty(type.ProcessSubmittedValue(_field, [], Post(new() { ["country"] = "GB" })).Cast<object>());
    }

    [Fact]
    public void Stored_IsNormalised()
    {
        var value = Stored(Create(), Manual(p => { p["line1"] = "  Flat   2 "; p["line3"] = "   "; }));

        Assert.Equal(["Flat 2", "10 High Street"], value!.Lines);
        Assert.Equal("LS1 4AP", value.Postcode);
        Assert.Equal("United Kingdom", value.Country);
    }

    [Fact]
    public void ForgedCountryName_IsIgnored_NameComesFromIso2()
    {
        var value = Stored(Create(), Manual(p => p["country"] = "GB"));

        Assert.Equal("United Kingdom", value!.Country);
    }

    [Fact]
    public void ConfigErrors_IdealPostcodesNonGb()
    {
        _options.Provider = ProviderKind.IdealPostcodes;

        Assert.Empty(Create().GetFieldConfigurationErrors(_field));
        var errors = Create(("Countries", "GB,IE")).GetFieldConfigurationErrors(_field);
        Assert.Single(errors);
    }

    [Fact]
    public void ConfigErrors_RequireLookupInCapture()
    {
        _options.Mode = LookupMode.Capture;

        Assert.Empty(Create().GetFieldConfigurationErrors(_field));
        var errors = Create(("RequireLookup", "true")).GetFieldConfigurationErrors(_field);
        Assert.Single(errors);
    }

    [Fact]
    public void RequireLookup_WithNoProviderConfigured_IsIgnored_AndFlagged()
    {
        // No provider means no proof can ever exist, so enforcing it would make the form unsubmittable.
        _options.Provider = null;
        var type = Create(("RequireLookup", "true"));

        Assert.Empty(Validate(type, Manual()));
        Assert.Single(type.GetFieldConfigurationErrors(_field));
    }

    [Fact]
    public void Mandatory_MissingPostcode_StoresNothing() =>
        Assert.Null(Stored(Create(), Manual(p => p.Remove("postcode"))));

    [Theory]
    [InlineData("line1", 100)]
    [InlineData("town", 60)]
    [InlineData("county", 60)]
    public void PartAtExactCap_Accepted(string part, int length) =>
        Assert.Empty(Validate(Create(), Manual(p => p[part] = new string('a', length))));

    [Theory]
    [InlineData("line1")]
    [InlineData("town")]
    public void HugePart_RefusedCheaply(string part)
    {
        var parts = Manual(p => p[part] = string.Join(' ', Enumerable.Repeat("a", 2_000_000)));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var errors = Validate(Create(), parts);

        Assert.Equal(["A part of the address is too long"], errors);
        Assert.True(watch.ElapsedMilliseconds < 500, "must not normalise or scan multi-megabyte input");
    }

    [Fact]
    public void HugeProviderIdAndProof_AreDroppedUnread_AndTheAddressStaysManual()
    {
        var huge = new string('a', 5_000_000);
        var value = Stored(Create(), Manual(p => { p["providerId"] = huge; p["proof"] = huge; }));

        Assert.Equal("manual", value!.Source);
        Assert.Null(value.ProviderId);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("10 High <b>Street")]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+44 20 7946 0000")]
    [InlineData("@SUM(A1)")]
    [InlineData("-cmd|' /C calc'!A0")]
    public void MarkupAndFormulaStarts_Refused(string value) =>
        Assert.Equal(["Remove any special characters from the address"], Validate(Create(), Manual(p => p["line2"] = value)));

    [Theory]
    [InlineData("-5 Mill Lane")]
    [InlineData("Unit 4 & 5 Mill Lane")]
    [InlineData("St Mary's Road")]
    public void OrdinaryPunctuation_Accepted(string value) =>
        Assert.Empty(Validate(Create(), Manual(p => p["line2"] = value)));

    [Fact]
    public void Stored_Json_EscapesHtmlSensitiveCharacters()
    {
        var stored = (string)Create().ProcessSubmittedValue(_field, [], Post(Manual(p => p["line2"] = "Unit 4 & 5 St Mary's +1"))).Cast<object>().Single();

        Assert.DoesNotContain("&", stored);
        Assert.DoesNotContain("'", stored);
        Assert.DoesNotContain("+", stored);
    }

    [Theory]
    [InlineData("\u3164")]
    [InlineData("\u115F\u1160")]
    [InlineData("\uFFA0")]
    [InlineData("\u2800")]
    public void InvisibleLetters_AreNotAnAddressLine(string blank) =>
        Assert.NotEmpty(Validate(Create(), Manual(p => p["line1"] = blank)));

    [Fact]
    public void ZeroWidthJoinerSequences_AreAllowed() =>
        Assert.Empty(Validate(Create(), Manual(p => p["county"] = "Cymru \U0001F3F4\U000E0067\U000E0062\U000E0077\U000E006C\U000E0073\U000E007F \U0001F468\u200D\U0001F469")));

    [Fact]
    public void NonFormRequest_IsTreatedAsEmpty_NotAnException()
    {
        // A headless JSON submit has no form to read, and HttpRequest.Form throws on it.
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        _field.Mandatory = true;

        var errors = Create().ValidateField(new Form(), _field, [], context, null!, null!).ToList();

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void RepeatedKey_UsesTheFirstValueOnly()
    {
        var context = Post(Manual());
        var form = context.Request.Form.ToDictionary(k => k.Key, k => k.Value);
        form[$"{_field.Id}.town"] = new StringValues(["Leeds", "Bradford"]);
        context.Request.Form = new FormCollection(form);

        Assert.Equal("Leeds", AddressValue.TryParse((string)Create().ProcessSubmittedValue(_field, [], context).Cast<object>().Single(), out var v) ? v!.Town : null);
    }

    [Fact]
    public void ForgedCountryNameKey_IsIgnored()
    {
        var value = Stored(Create(), Manual(p => { p["countryName"] = "Narnia"; p["country"] = "GB"; }));

        Assert.Equal("United Kingdom", value!.Country);
    }

    [Fact]
    public void SubmitHooks_AgreeOnOneEvaluation()
    {
        var type = Create();
        var context = Post(Manual());
        _field.Mandatory = true;

        _ = type.ProcessSubmittedValue(_field, [], context).ToList();
        context.Request.Form = new FormCollection([]);

        // Forms runs both hooks on one request (spike S1); the second reuses the first's answer instead of re-scanning input.
        Assert.Empty(type.ValidateField(new Form(), _field, [], context, null!, null!));
    }

    [Fact]
    public void Identity_IsPermanent()
    {
        var type = Create();

        Assert.Equal(new Guid("7c1e8b0a-3d52-4f6e-9a47-2b8d5c0e1f93"), AddressLookupFieldType.TypeId);
        Assert.Equal(AddressLookupFieldType.TypeId, type.Id);
        Assert.Equal("communityAddressLookup", AddressLookupFieldType.FieldAlias);
        Assert.Equal("FieldType.AddressLookup.cshtml", type.FieldTypeViewName);
        Assert.Equal("Community.AddressLookup.Preview", type.PreviewView);
        Assert.Equal(Umbraco.Forms.Core.Enums.FieldDataType.LongString, type.DataType);
        Assert.True(type.SupportsMandatory);
        Assert.False(type.SupportsRegex);
    }
}
