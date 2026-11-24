using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Tests.Support;

namespace Umbraco.Community.AddressLookup.Tests;

/// <summary>What the field view is given (design 3.2, 4.3, 5.4). The view itself only prints these values.</summary>
public class AddressFieldRendererTests
{
    private const string ServerKey = "lq-SENTINEL-KEY"; // gitleaks:allow
    private const string BrowserKey = "lq-SENTINEL-BROWSER"; // gitleaks:allow

    private readonly Guid _formId = Guid.NewGuid();
    private readonly Guid _fieldId = Guid.NewGuid();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly AddressLookupOptions _options = new() { Provider = ProviderKind.Stub };
    private readonly LookupTicketService _tickets;
    private readonly AddressProofService _proofs;
    private bool _providerRegistered = true;

    public AddressFieldRendererTests()
    {
        var protection = DataProtectionProvider.Create("address-lookup-tests");
        _tickets = new LookupTicketService(protection, _time);
        _proofs = new AddressProofService(protection, _time);
    }

    private AddressFieldRender Render(
        Dictionary<string, string>? settings = null, bool mandatory = false, IFormCollection? form = null, bool diagnostics = false, string? stored = null)
    {
        var provider = Substitute.For<IAddressLookupProvider>();
        IAddressLookupProvider[] providers = _providerRegistered ? [provider] : [];
        var renderer = new AddressFieldRenderer(new TestOptionsMonitor<AddressLookupOptions>(_options), _tickets, _proofs, providers);
        return renderer.Create(new AddressFieldRenderRequest(
            _formId, _fieldId.ToString(), "field_1", "Home address", mandatory, settings ?? [], form, diagnostics, stored));
    }

    private IFormCollection Posted(Dictionary<string, string> parts) =>
        new FormCollection(parts.ToDictionary(p => $"{_fieldId}.{p.Key}", p => new StringValues(p.Value)));

    [Fact]
    public void Proxy_IssuesTicketForThisField()
    {
        var render = Render();

        Assert.True(render.LookupEnabled);
        Assert.False(render.CaptureEnabled);
        Assert.True(_tickets.TryRead(render.Ticket, out var ticket));
        Assert.Equal(_formId, ticket.FormId);
        Assert.Equal(_fieldId, ticket.FieldId);
    }

    [Fact]
    public void NoProvider_ManualOnly_NoTicket()
    {
        _providerRegistered = false;
        _options.Provider = null;

        var render = Render();

        Assert.False(render.LookupEnabled);
        Assert.Null(render.Ticket);
        Assert.True(render.ShowManual);
    }

    [Fact]
    public void Capture_UsesBrowserKey_NeverServerKey()
    {
        _options.Provider = ProviderKind.Loqate;
        _options.Mode = LookupMode.Capture;
        _options.Loqate.ApiKey = ServerKey;
        _options.Loqate.CaptureKey = BrowserKey;

        var render = Render();

        Assert.True(render.CaptureEnabled);
        Assert.False(render.LookupEnabled);
        Assert.Null(render.Ticket);
        Assert.Equal(BrowserKey, render.CaptureKey);
        Assert.Equal("4.05", render.CaptureScriptVersion);
        Assert.DoesNotContain(ServerKey, render.ToString());
        Assert.DoesNotContain(ServerKey, render.CaptureKey);
    }

    [Theory]
    [InlineData(ServerKey + "\n")]
    [InlineData(" " + ServerKey)]
    public void Capture_KeyThatIsTheServerKeyAfterTrim_IsNeverRendered(string captureKey)
    {
        _options.Provider = ProviderKind.Loqate;
        _options.Mode = LookupMode.Capture;
        _options.Loqate.ApiKey = ServerKey;
        _options.Loqate.CaptureKey = captureKey;

        var render = Render();

        Assert.False(render.CaptureEnabled);
        Assert.Null(render.CaptureKey);
    }

    [Fact]
    public void Capture_WithoutCaptureKey_FallsBackToManual()
    {
        _options.Provider = ProviderKind.Loqate;
        _options.Mode = LookupMode.Capture;
        _options.Loqate.ApiKey = ServerKey;

        var render = Render();

        Assert.False(render.CaptureEnabled);
        Assert.False(render.LookupEnabled);
        Assert.True(render.ShowManual);
    }

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("4.05", "4.05")]
    [InlineData("4.05\"><script>", "4.05")]
    [InlineData("../x", "4.05")]
    public void Capture_ScriptVersion_IsDigitsAndDotsOnly(string configured, string expected)
    {
        _options.Provider = ProviderKind.Loqate;
        _options.Mode = LookupMode.Capture;
        _options.Loqate.CaptureKey = BrowserKey;
        _options.Loqate.CaptureScriptVersion = configured;

        var render = Render();

        // The version is placed in a script URL, so anything that is not a plain version falls back to the default.
        Assert.Equal(expected, render.CaptureScriptVersion);
    }

    [Fact]
    public void Settings_AreReadByName_WithDefaults()
    {
        var render = Render();

        Assert.Equal(["GB"], render.Countries);
        Assert.True(render.AllowManualEntry);
        Assert.False(render.ShowCountry);
        Assert.Equal(string.Empty, render.Placeholder);

        var custom = Render(new()
        {
            ["Countries"] = "gb, ie",
            ["AllowManualEntry"] = "false",
            ["ShowCountry"] = "true",
            ["Placeholder"] = "Start typing your postcode",
        });

        Assert.Equal(["GB", "IE"], custom.Countries);
        Assert.False(custom.AllowManualEntry);
        Assert.True(custom.ShowCountry);
        Assert.Equal("Start typing your postcode", custom.Placeholder);
    }

    [Fact]
    public void BadFieldName_NoTicket()
    {
        var renderer = new AddressFieldRenderer(new TestOptionsMonitor<AddressLookupOptions>(_options), _tickets, _proofs, [Substitute.For<IAddressLookupProvider>()]);

        var render = renderer.Create(new AddressFieldRenderRequest(_formId, "not-a-guid", "field_1", "Home", false, new Dictionary<string, string>(), null, false, null));

        Assert.False(render.LookupEnabled);
        Assert.Null(render.Ticket);
    }

    [Fact]
    public void Posted_Values_AreEchoedAndManualShown()
    {
        var render = Render(form: Posted(new()
        {
            ["line1"] = "Flat 2", ["town"] = "Leeds", ["postcode"] = "LS1 4AP", ["country"] = "GB",
            ["providerId"] = "stub-a2", ["proof"] = "abc",
        }));

        Assert.True(render.ShowManual);
        Assert.Equal("Flat 2", render.Posted.Line1);
        Assert.Equal("Leeds", render.Posted.Town);
        Assert.Equal("stub-a2", render.Posted.ProviderId);
        Assert.Equal("abc", render.Posted.Proof);
    }

    [Fact]
    public void Posted_Oversize_IsTruncatedAndProofDropped()
    {
        var render = Render(form: Posted(new()
        {
            ["line1"] = new string('a', 5000), ["proof"] = new string('p', 5000), ["providerId"] = new string('i', 500),
        }));

        Assert.Equal(512, render.Posted.Line1.Length);
        Assert.Equal(string.Empty, render.Posted.Proof);
        Assert.Equal(string.Empty, render.Posted.ProviderId);
    }

    [Fact]
    public void NoPost_ManualHiddenUntilAsked_WhenLookupWorks()
    {
        var render = Render();

        Assert.False(render.ShowManual);
        Assert.Equal(string.Empty, render.Posted.Line1);
    }

    [Fact]
    public void HeadlessRequest_FormMissing_DoesNotThrow()
    {
        var render = Render(form: null);

        Assert.NotNull(render);
    }

    [Fact]
    public void ConfigurationErrors_OnlyWhenDiagnosticsAllowed()
    {
        var settings = new Dictionary<string, string> { ["Countries"] = "GB,IE" };
        _options.Provider = ProviderKind.IdealPostcodes;

        Assert.Empty(Render(settings, diagnostics: false).ConfigurationErrors);
        Assert.Single(Render(settings, diagnostics: true).ConfigurationErrors);
    }

    [Fact]
    public void ToString_PrintsNothingAboutTheAddress()
    {
        var render = Render(form: Posted(new() { ["line1"] = "1 Sentinel Street", ["postcode"] = "LS1 4AP" }));

        var text = render.ToString();

        Assert.DoesNotContain("Sentinel", text);
        Assert.DoesNotContain("LS1", text);
    }

    [Fact]
    public void Stored_Lookup_Value_RefillsFields_WhenNothingPosted_AndKeepsVerification()
    {
        var picked = new Umbraco.Community.AddressLookup.Model.AddressValue(
            ["Flat 2", "10 High Street"], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "stub", "stub-a2", "lookup");

        // The submit path tags a lookup it verified; that tag is what earns the stored value a fresh proof.
        var render = Render(stored: picked.ToJson(_proofs.IssueStoredTag(picked.Normalise(), _fieldId)));

        Assert.True(render.ShowManual);
        Assert.Equal("Flat 2", render.Posted.Line1);
        Assert.Equal("10 High Street", render.Posted.Line2);
        Assert.Equal("stub-a2", render.Posted.ProviderId);
        Assert.True(_proofs.Verify(render.Posted.Proof, picked.Normalise(), _fieldId));
    }

    [Fact]
    public void Stored_ClaimingLookup_WithoutTheServersTag_GetsNoProof()
    {
        // Prefill content (raw form state, member prefill, a FormPrePopulate handler) is not something the server issued, so
        // rendering it must not turn it into a signed proof.
        var forged = new Umbraco.Community.AddressLookup.Model.AddressValue(
            ["Anywhere"], "Leeds", null, "LS1 4AP", "GB", null, "stub", "forged-id", "lookup");

        var render = Render(stored: forged.ToJson());

        Assert.Equal("Anywhere", render.Posted.Line1);
        Assert.Equal(string.Empty, render.Posted.Proof);
        Assert.Equal(string.Empty, render.Posted.ProviderId);
    }

    [Fact]
    public void OnlyCountryPosted_ByAnotherFieldsFailedSubmit_CountsAsNothingPosted()
    {
        // The view always posts {field}.country (and the hidden provider inputs), so a failed submit caused by another field
        // must not open the manual panel or block the stored-value refill.
        var form = Posted(new() { ["country"] = "GB", ["providerId"] = string.Empty, ["proof"] = string.Empty });

        Assert.False(Render(form: form).ShowManual);

        var manual = new Umbraco.Community.AddressLookup.Model.AddressValue(
            ["1 High Street"], "Leeds", null, "LS1 4AP", "GB", null, null, null, "manual");
        var refilled = Render(form: form, stored: manual.ToJson());
        Assert.Equal("1 High Street", refilled.Posted.Line1);
        Assert.True(refilled.ShowManual);
    }

    [Fact]
    public void Stored_Manual_Value_RefillsWithoutProof()
    {
        var manual = new Umbraco.Community.AddressLookup.Model.AddressValue(
            ["1 High Street"], "Leeds", null, "LS1 4AP", "GB", null, null, null, "manual");

        var render = Render(stored: manual.ToJson());

        Assert.Equal("1 High Street", render.Posted.Line1);
        Assert.Equal(string.Empty, render.Posted.Proof);
    }

    [Fact]
    public void Posted_Wins_OverStored()
    {
        var render = Render(form: Posted(new() { ["line1"] = "From the post" }), stored: "{\"v\":1,\"lines\":[\"Old\"],\"town\":\"X\",\"postcode\":\"LS1 4AP\",\"countryIso2\":\"GB\",\"source\":\"manual\"}");

        Assert.Equal("From the post", render.Posted.Line1);
    }

    [Fact]
    public void Stored_Garbage_IsIgnored()
    {
        var render = Render(stored: "not json");

        Assert.True(render.Posted.IsEmpty);
    }
}
