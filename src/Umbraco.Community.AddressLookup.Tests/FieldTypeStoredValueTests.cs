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

/// <summary>
/// What reaches storage. Forms stores the value of <c>ConvertToRecord</c>, and on two paths (a FormPrePopulate handler is
/// registered; the headless Delivery API) it hands that method a value nobody validated, so the type has to validate it there
/// as well.
/// </summary>
public class FieldTypeStoredValueTests
{
    private const string Formula = "=HYPERLINK(\"http://attacker.example\")";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
    private readonly Field _field = new() { Id = Guid.NewGuid(), Settings = new Dictionary<string, string>() };
    private readonly AddressLookupOptions _options = new() { Provider = ProviderKind.Stub };
    private readonly AddressProofService _proofs;

    public FieldTypeStoredValueTests()
    {
        _proofs = new AddressProofService(DataProtectionProvider.Create("address-lookup-tests"), _time);
    }

    private AddressLookupFieldType Create(params (string Key, string Value)[] settings)
    {
        foreach (var (key, value) in settings)
        {
            _field.Settings[key] = value;
        }

        var provider = Substitute.For<IAddressLookupProvider>();
        provider.Alias.Returns("stub");
        return new AddressLookupFieldType(new TestOptionsMonitor<AddressLookupOptions>(_options), _proofs, [provider]);
    }

    private static AddressValue Manual(Func<AddressValue, AddressValue>? edit = null)
    {
        var value = new AddressValue(["Flat 2", "10 High Street"], "Leeds", null, "LS1 4AP", "GB", null, null, null, "manual");
        return edit is null ? value : edit(value);
    }

    private static AddressValue ClaimingLookup() =>
        Manual(v => v with { Source = "lookup", Provider = "stub", ProviderId = "forged-id", Country = "Atlantis" });

    private static HttpContext Headless()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        return context;
    }

    // A FormPrePopulate handler makes Forms read Request.Form[fieldId] itself, so a browser can post that key too.
    private HttpContext FormWithRawKey(string raw, Dictionary<string, string>? parts = null)
    {
        var form = (parts ?? new Dictionary<string, string>
        {
            ["line1"] = "Flat 2", ["line2"] = "10 High Street", ["town"] = "Leeds", ["postcode"] = "LS1 4AP", ["country"] = "GB",
        }).ToDictionary(p => $"{_field.Id}.{p.Key}", p => new StringValues(p.Value));
        form[_field.Id.ToString()] = raw;

        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(form);
        return context;
    }

    private IReadOnlyList<string> Validate(AddressLookupFieldType type, HttpContext context, object[] posted, bool mandatory = true)
    {
        _field.Mandatory = mandatory;
        return type.ValidateField(new Form(), _field, posted, context, null!, null!).ToList();
    }

    private static AddressValue Parse(object stored)
    {
        Assert.True(AddressValue.TryParse((string)stored, out var value));
        return value!;
    }

    // --- F1: the form path with a FormPrePopulate handler ---

    [Fact]
    public void RawFieldIdValue_IsNeverStored_TheEvaluatedPartsAre()
    {
        var type = Create();
        var context = FormWithRawKey(Formula);

        Assert.Empty(Validate(type, context, [Formula]));
        var stored = type.ConvertToRecord(_field, [Formula], context).Cast<object>().Single();

        var value = Parse(stored);
        Assert.Equal(["Flat 2", "10 High Street"], value.Lines);
        Assert.DoesNotContain("HYPERLINK", (string)stored);
    }

    [Fact]
    public void RawFieldIdValue_WithInvalidParts_StoresNothing()
    {
        var type = Create();
        var forged = ClaimingLookup().ToJson();
        var context = FormWithRawKey(forged, new() { ["line1"] = Formula, ["town"] = "Leeds", ["postcode"] = "LS1 4AP", ["country"] = "GB" });

        Assert.NotEmpty(Validate(type, context, [forged]));

        Assert.Empty(type.ConvertToRecord(_field, [forged], context));
    }

    [Fact]
    public void PartsAreStored_EvenWhenFormsHandsOverNoValueAtAll()
    {
        // With a FormPrePopulate handler Forms never calls ProcessSubmittedValue, and our parts are not under the field id.
        var type = Create();
        var context = FormWithRawKey(string.Empty);

        Assert.Empty(Validate(type, context, []));

        Assert.Equal("Leeds", Parse(type.ConvertToRecord(_field, [], context).Cast<object>().Single()).Town);
    }

    // --- F1: headless submits (Delivery API) ---

    [Fact]
    public void Headless_ValidAddress_Mandatory_PassesAndIsStoredManual()
    {
        var type = Create();
        var context = Headless();
        var posted = new object[] { Manual().ToJson() };

        Assert.Empty(Validate(type, context, posted));

        var value = Parse(type.ConvertToRecord(_field, posted, context).Cast<object>().Single());
        Assert.Equal(("Leeds", "LS1 4AP", "manual"), (value.Town, value.Postcode, value.Source));
        Assert.Equal("United Kingdom", value.Country);
    }

    [Fact]
    public void Headless_ValidAddress_Optional_PassesAndIsStored()
    {
        var type = Create();
        var context = Headless();
        var posted = new object[] { Manual().ToJson() };

        Assert.Empty(Validate(type, context, posted, mandatory: false));

        Assert.Single(type.ConvertToRecord(_field, posted, context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Headless_NothingPosted_IsMissingOnlyWhenMandatory(bool mandatory)
    {
        var type = Create();
        var context = Headless();

        var errors = Validate(type, context, [], mandatory);

        Assert.Equal(mandatory ? 3 : 0, errors.Count);
        Assert.Empty(type.ConvertToRecord(_field, [], context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Headless_FormulaAndMarkup_AreRefused_AndNotStored(bool mandatory)
    {
        var type = Create();
        foreach (var evil in new[] { Formula, "<script>alert(1)</script>" })
        {
            var context = Headless();
            var posted = new object[] { Manual(v => v with { Lines = ["1 High Street", evil] }).ToJson() };

            Assert.Equal(["Remove any special characters from the address"], Validate(type, context, posted, mandatory));
            Assert.Empty(type.ConvertToRecord(_field, posted, context));
        }
    }

    [Fact]
    public void Headless_OversizePart_IsRefused()
    {
        var type = Create();
        var posted = new object[] { Manual(v => v with { Town = new string('a', 600) }).ToJson() };

        Assert.Contains("A part of the address is too long", Validate(type, Headless(), posted));
    }

    [Fact]
    public void Headless_CountryOutsideTheFieldsList_IsRefused()
    {
        var posted = new object[] { Manual(v => v with { CountryIso2 = "FR" }).ToJson() };

        Assert.Contains("Choose a country from the list", Validate(Create(), Headless(), posted));
    }

    [Fact]
    public void Headless_UnreadableValue_IsRefused_AndNotStored()
    {
        var type = Create();
        var context = Headless();
        var posted = new object[] { "10 High Street, Leeds" };

        Assert.NotEmpty(Validate(type, context, posted, mandatory: false));
        Assert.Empty(type.ConvertToRecord(_field, posted, context));
    }

    [Fact]
    public void Headless_ForgedLookupSource_IsStoredAsManual()
    {
        var type = Create();
        var context = Headless();
        var posted = new object[] { ClaimingLookup().ToJson() };

        Assert.Empty(Validate(type, context, posted));

        var value = Parse(type.ConvertToRecord(_field, posted, context).Cast<object>().Single());
        Assert.Equal("manual", value.Source);
        Assert.Null(value.Provider);
        Assert.Null(value.ProviderId);
        Assert.Equal("United Kingdom", value.Country);
    }

    [Fact]
    public void Headless_ForgedLookupSource_FailsRequireLookup()
    {
        var type = Create(("RequireLookup", "true"));

        var errors = Validate(type, Headless(), [ClaimingLookup().ToJson()]);

        Assert.Equal(["Select an address from the list"], errors);
    }

    // --- F1: re-conversion of a value that is already in form state (multi-page forms, workflows) ---

    [Fact]
    public void ConvertToRecord_WithoutAnEvaluation_RefusesFormulaAndGarbage()
    {
        var type = Create();
        var formula = Manual(v => v with { Lines = [Formula] }).ToJson();

        Assert.Empty(type.ConvertToRecord(_field, [formula], Headless()));
        Assert.Empty(type.ConvertToRecord(_field, ["not json"], Headless()));
        Assert.Empty(type.ConvertToRecord(_field, [Formula], Headless()));
    }

    [Fact]
    public void ConvertToRecord_WithoutAnEvaluation_DowngradesAnUnattributedLookupClaim()
    {
        var type = Create();

        var value = Parse(type.ConvertToRecord(_field, [ClaimingLookup().ToJson()], Headless()).Cast<object>().Single());

        Assert.Equal("manual", value.Source);
        Assert.Null(value.ProviderId);
    }

    [Fact]
    public void ConvertToRecord_StoresAtMostOneValue()
    {
        var json = Manual().ToJson();

        Assert.Single(Create().ConvertToRecord(_field, [json, json], Headless()));
    }

    // --- F2: the keyed tag that makes a stored lookup trustworthy ---

    private AddressValue Picked() => new(
        ["Flat 2", "10 High Street"], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "stub", "stub-a2", "lookup");

    private HttpContext FormWithProof(string proof)
    {
        var parts = new Dictionary<string, string>
        {
            ["line1"] = "Flat 2", ["line2"] = "10 High Street", ["town"] = "Leeds", ["postcode"] = "LS1 4AP", ["country"] = "GB",
            ["providerId"] = "stub-a2", ["proof"] = proof,
        };
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(parts.ToDictionary(p => $"{_field.Id}.{p.Key}", p => new StringValues(p.Value)));
        return context;
    }

    [Fact]
    public void SubmittedLookup_IsStoredWithATag_ThatSurvivesReConversionInANewRequest()
    {
        var type = Create();
        var submitted = (string)type.ProcessSubmittedValue(_field, [], FormWithProof(_proofs.Issue(Picked(), _field.Id))).Cast<object>().Single();

        // A multi-page form's final POST converts the state kept from an earlier page, with nothing evaluated on that request.
        var converted = Parse(type.ConvertToRecord(_field, [submitted], Headless()).Cast<object>().Single());

        Assert.True(AddressValue.TryParse(submitted, out _, out var tag));
        Assert.False(string.IsNullOrEmpty(tag));
        Assert.Equal(("lookup", "stub", "stub-a2"), (converted.Source, converted.Provider, converted.ProviderId));
    }

    [Fact]
    public void ManualSubmit_CarriesNoTag()
    {
        var stored = (string)Create().ProcessSubmittedValue(_field, [], FormWithRawKey(string.Empty)).Cast<object>().Single();

        Assert.True(AddressValue.TryParse(stored, out _, out var tag));
        Assert.Null(tag);
        Assert.DoesNotContain("\"t\"", stored);
    }

    [Fact]
    public void TaggedValue_EditedAfterwards_FallsBackToManual()
    {
        var type = Create();
        var submitted = (string)type.ProcessSubmittedValue(_field, [], FormWithProof(_proofs.Issue(Picked(), _field.Id))).Cast<object>().Single();

        var edited = submitted.Replace("Leeds", "York");

        Assert.Equal("manual", Parse(type.ConvertToRecord(_field, [edited], Headless()).Cast<object>().Single()).Source);
    }

    [Fact]
    public void TagFromAnotherField_DoesNotMakeAValueALookup()
    {
        var type = Create();
        var elsewhere = Picked();
        var stolen = elsewhere.ToJson(_proofs.IssueStoredTag(elsewhere.Normalise(), Guid.NewGuid()));

        Assert.Equal("manual", Parse(type.ConvertToRecord(_field, [stolen], Headless()).Cast<object>().Single()).Source);
    }

    [Fact]
    public void TaggedValue_Headless_SatisfiesRequireLookup()
    {
        // A client that re-posts a value it read back from an entry keeps its verification; one that made the value up cannot.
        var type = Create(("RequireLookup", "true"));
        var picked = Picked();
        var tagged = picked.ToJson(_proofs.IssueStoredTag(picked.Normalise(), _field.Id));

        Assert.Empty(Validate(type, Headless(), [tagged]));
    }

    [Fact]
    public void Tag_IsIgnoredByAReaderThatDoesNotKnowIt_SoTheSchemaVersionStaysOne()
    {
        var picked = Picked();
        var json = picked.ToJson("tag-value");

        Assert.Contains("\"v\":1", json);
        Assert.True(AddressValue.TryParse(json, out var parsed));
        Assert.Equal(picked.Town, parsed!.Town);
    }
}
