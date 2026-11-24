using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;

namespace Umbraco.Community.AddressLookup.FieldTypes;

/// <summary>What the field view needs from the page: Forms' own values for one field, plus the posted form on a re-render.</summary>
public sealed record AddressFieldRenderRequest(
    Guid FormId,
    string FieldName,
    string HtmlId,
    string Caption,
    bool Mandatory,
    IDictionary<string, string>? Settings,
    IFormCollection? PostedForm,
    bool AllowDiagnostics,
    string? StoredValue);

/// <summary>The parts a visitor posted, echoed back when Forms re-renders the page after a validation error.</summary>
public sealed record PostedAddress(
    string Line1, string Line2, string Line3, string Town, string County, string Postcode, string Country,
    string ProviderId, string Proof)
{
    public static PostedAddress Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    // The view always posts the country and the two hidden provider inputs, so they say nothing about whether the visitor typed
    // or picked anything. Same rule as AddressFormReader.
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Line1) && string.IsNullOrWhiteSpace(Line2) && string.IsNullOrWhiteSpace(Line3)
        && string.IsNullOrWhiteSpace(Town) && string.IsNullOrWhiteSpace(County) && string.IsNullOrWhiteSpace(Postcode);

    // An address is personal data and a record prints every member (design 5.1).
    public override string ToString() => nameof(PostedAddress);
}

/// <summary>Everything the field view prints. It decides nothing itself, so the view stays free of logic and services (pitfall P5).</summary>
public sealed record AddressFieldRender(
    string Name,
    string HtmlId,
    string Caption,
    bool Mandatory,
    string Placeholder,
    bool AllowManualEntry,
    bool ShowCountry,
    IReadOnlyList<string> Countries,
    bool LookupEnabled,
    bool CaptureEnabled,
    string? Ticket,
    string? CaptureKey,
    string CaptureScriptVersion,
    bool ShowManual,
    PostedAddress Posted,
    IReadOnlyList<string> ConfigurationErrors)
{
    // The ticket and the posted address do not belong in a log line.
    public override string ToString() => $"AddressFieldRender {{ Name = {Name}, LookupEnabled = {LookupEnabled}, CaptureEnabled = {CaptureEnabled} }}";
}

/// <summary>
/// Builds <see cref="AddressFieldRender"/> for the field view (design 3.2, 4.3). It sits behind the view so the rules for
/// the ticket, Capture mode and the echoed values are unit-tested, and so the view only prints.
/// </summary>
public sealed partial class AddressFieldRenderer(
    IOptionsMonitor<AddressLookupOptions> options, LookupTicketService tickets, AddressProofService proofs,
    IEnumerable<IAddressLookupProvider> providers)
{
    // Echoed values are the visitor's own post, but a re-render is not the place to reflect megabytes back.
    private const int MaxEchoed = 512;
    private const int MaxProviderId = 200;
    private const int MaxProof = 4096;
    private const int MaxStored = 8192;
    private const string DefaultCaptureVersion = "4.05";

    private readonly bool _hasProvider = providers.Any();

    public AddressFieldRender Create(AddressFieldRenderRequest request)
    {
        var settings = options.CurrentValue;
        var countries = AddressFieldSettings.ParseCountries(AddressFieldSettings.Get(request.Settings, "Countries"));
        var fieldId = Guid.TryParse(request.FieldName, out var parsed) ? parsed : (Guid?)null;
        var posted = Read(request.PostedForm, request.FieldName);

        // Going Back in a multi-page form posts the other page's inputs, so nothing of ours comes back; Forms keeps the value
        // it stored for this field and hands it to the view, and that is what the visitor sees again.
        if (posted.IsEmpty && fieldId is not null)
        {
            posted = FromStored(request.StoredValue, fieldId.Value) ?? posted;
        }
        var proxy = settings is { Mode: LookupMode.Proxy, Provider: not null } && _hasProvider && fieldId is not null;
        // Checked here as well as at start-up, so a key reloaded from configuration can never put a server key in a page.
        var capture = settings is { Mode: LookupMode.Capture, Provider: ProviderKind.Loqate }
            && !string.IsNullOrWhiteSpace(settings.Loqate.CaptureKey)
            && !IsServerKey(settings, settings.Loqate.CaptureKey.Trim());

        var lookup = proxy || capture;
        return new AddressFieldRender(
            request.FieldName,
            request.HtmlId,
            request.Caption,
            request.Mandatory,
            AddressFieldSettings.Get(request.Settings, "Placeholder") ?? string.Empty,
            AddressFieldSettings.IsOn(AddressFieldSettings.Get(request.Settings, "AllowManualEntry"), whenMissing: true),
            AddressFieldSettings.IsOn(AddressFieldSettings.Get(request.Settings, "ShowCountry")),
            countries,
            proxy,
            capture,
            proxy ? tickets.Issue(request.FormId, fieldId!.Value) : null,
            capture ? settings.Loqate.CaptureKey!.Trim() : null,
            ScriptVersion(settings.Loqate.CaptureScriptVersion),
            // With no lookup there is nothing to open, and after a failed submit the visitor needs to see what they typed.
            !lookup || !posted.IsEmpty,
            posted,
            request.AllowDiagnostics ? AddressFieldConfiguration.Errors(settings, request.Settings) : []);
    }

    private static bool IsServerKey(AddressLookupOptions settings, string key) =>
        key == settings.Loqate.ApiKey?.Trim() || key == settings.IdealPostcodes.ApiKey?.Trim();

    // The version is placed in a script URL, so it is digits and dots or the default.
    private static string ScriptVersion(string? configured) =>
        configured is not null && VersionPattern().IsMatch(configured) ? configured : DefaultCaptureVersion;

    // A fresh proof is issued only for a value carrying the server's own stored tag, which the submit path adds to a verified
    // lookup. Anything else that claims "lookup" (raw form state, member prefill, a FormPrePopulate handler) is content the
    // server cannot attribute to its own Retrieve, and signing it would make this view a proof oracle.
    private PostedAddress? FromStored(string? stored, Guid fieldId)
    {
        if (string.IsNullOrWhiteSpace(stored) || stored.Length > MaxStored || !AddressValue.TryParse(stored, out var value) || value is null)
        {
            return null;
        }

        AddressValue.TryParse(stored, out _, out var tag);
        var verified = proofs.VerifyStoredTag(tag, value, fieldId);
        string Part(string? text) => text is { Length: > MaxEchoed } ? text[..MaxEchoed] : text ?? string.Empty;
        string Line(int i) => i < value.Lines.Count ? Part(value.Lines[i]) : string.Empty;

        return new PostedAddress(
            Line(0), Line(1), Line(2), Part(value.Town), Part(value.County), Part(value.Postcode), Part(value.CountryIso2),
            verified ? value.ProviderId! : string.Empty,
            verified ? proofs.Issue(value, fieldId) : string.Empty);
    }

    private static PostedAddress Read(IFormCollection? form, string fieldName)
    {
        // A headless submit or a plain GET has no form to echo.
        if (form is null || form.Count == 0)
        {
            return PostedAddress.Empty;
        }

        string Part(string key, int max = MaxEchoed)
        {
            var value = form[$"{fieldName}.{key}"].FirstOrDefault() ?? string.Empty;
            return value.Length > max ? value[..max] : value;
        }

        // Dropped, not cut: a truncated proof or id never verifies and only looks like one.
        string Whole(string key, int max)
        {
            var value = form[$"{fieldName}.{key}"].FirstOrDefault() ?? string.Empty;
            return value.Length > max ? string.Empty : value;
        }

        return new PostedAddress(
            Part("line1"), Part("line2"), Part("line3"), Part("town"), Part("county"), Part("postcode"), Part("country"),
            Whole("providerId", MaxProviderId), Whole("proof", MaxProof));
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){0,3}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex VersionPattern();
}

/// <summary>Per-field settings that make the field misbehave rather than fail loudly (shared by the field type and the view).</summary>
public static class AddressFieldConfiguration
{
    public static IReadOnlyList<string> Errors(AddressLookupOptions settings, IDictionary<string, string>? fieldSettings)
    {
        var errors = new List<string>();
        var countries = AddressFieldSettings.ParseCountries(AddressFieldSettings.Get(fieldSettings, "Countries"));

        if (settings.Provider == ProviderKind.IdealPostcodes && countries.Any(c => c != "GB"))
        {
            errors.Add("Ideal Postcodes only covers Great Britain. Set Countries to GB, or use Loqate.");
        }

        if (AddressFieldSettings.IsOn(AddressFieldSettings.Get(fieldSettings, "RequireLookup")))
        {
            if (settings.Mode == LookupMode.Capture)
            {
                errors.Add("Require lookup cannot be used in Capture mode, because browser-side results cannot be verified.");
            }
            else if (settings.Provider is null)
            {
                errors.Add("Require lookup needs a provider. Set AddressLookup:Provider, or turn it off.");
            }
        }

        return errors;
    }
}
