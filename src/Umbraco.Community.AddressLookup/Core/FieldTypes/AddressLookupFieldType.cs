using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Forms.Core;
using Umbraco.Forms.Core.Attributes;
using Umbraco.Forms.Core.Enums;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.AddressLookup.FieldTypes;

/// <summary>
/// The Forms "Address lookup" field (design 3.2, 3.3, 5.5). One field renders a search box and manual inputs, and stores
/// one JSON value. The field stays thin: the lookup proxy does the provider work, and this type only decides, on submit,
/// whether what the browser posted is a valid address and whether it is the one a provider returned.
/// </summary>
public sealed class AddressLookupFieldType : FieldType
{
    /// <summary>Permanent: stored forms refer to a field type by this id.</summary>
    public static readonly Guid TypeId = new("7c1e8b0a-3d52-4f6e-9a47-2b8d5c0e1f93");

    /// <summary>Permanent. Forms uses it in form definitions and as the CSS class of the rendered field.</summary>
    public const string FieldAlias = "communityAddressLookup";

    private const int MaxLine = 100;
    private const int MaxTown = 60;
    private const int MaxCounty = 60;
    private const int MaxPostcode = 10;
    private const int HardCeiling = 512;

    private readonly IOptionsMonitor<AddressLookupOptions> _options;
    private readonly AddressProofService _proofs;
    private readonly IReadOnlyList<IAddressLookupProvider> _providers;

    public AddressLookupFieldType(
        IOptionsMonitor<AddressLookupOptions> options, AddressProofService proofs, IEnumerable<IAddressLookupProvider> providers)
    {
        _options = options;
        _proofs = proofs;
        _providers = [.. providers];

        Id = TypeId;
        Alias = FieldAlias;
        Name = "Address lookup";
        Description = "Look up a UK address by postcode or address, or enter it by hand.";
        Icon = "icon-map-location";
        Group = "Simple";
        DataType = FieldDataType.LongString;
        SupportsMandatory = true;
        SupportsRegex = false;
        FieldTypeViewName = "FieldType.AddressLookup.cshtml";
        PreviewView = "Community.AddressLookup.Preview";
    }

    // The properties only declare the settings to Forms. The per-field values are on Field.Settings, read by key.

    [Setting("Countries", Description = "Two-letter country codes the visitor may choose, separated by commas. For example GB or GB,IE.", View = "Umb.PropertyEditorUi.TextBox", DefaultValue = "GB")]
    public string Countries { get; set; } = AddressFieldSettings.DefaultCountry;

    [Setting("Placeholder", Description = "Text shown in the search box before the visitor types.", View = "Umb.PropertyEditorUi.TextBox", SupportsPlaceholders = false)]
    public string Placeholder { get; set; } = string.Empty;

    [Setting("AllowManualEntry", Description = "Show an \"Enter address manually\" button.", View = "Umb.PropertyEditorUi.Toggle", DefaultValue = "true")]
    public string AllowManualEntry { get; set; } = "true";

    [Setting("RequireLookup", Description = "Refuse addresses that were not picked from the list. Proxy mode only.", View = "Umb.PropertyEditorUi.Toggle", DefaultValue = "false")]
    public string RequireLookup { get; set; } = "false";

    [Setting("ShowCountry", Description = "Show a country choice. Off uses the first country.", View = "Umb.PropertyEditorUi.Toggle", DefaultValue = "false")]
    public string ShowCountry { get; set; } = "false";

    /// <summary>Settings that make a field misbehave rather than fail loudly. Forms has no per-field hook, so the field view reports these.</summary>
    public IReadOnlyList<string> GetFieldConfigurationErrors(Field field) =>
        AddressFieldConfiguration.Errors(_options.CurrentValue, field.Settings);

    public override IEnumerable<string> ValidateField(
        Form form, Field field, IEnumerable<object> postedValues, HttpContext context,
        IPlaceholderParsingService placeholderParsingService, IFieldTypeStorage fieldTypeStorage)
    {
        var (_, errors, _) = EvaluateWithState(field, context);

        // Forms calls this for a field hidden by a condition too, and the base implementation is what skips it: it returns no
        // error for a hidden mandatory field. Our override replaces it, so a hidden mandatory field would block the submit
        // (also when the visitor typed an address and then hid the field again). For a mandatory field, the base answering
        // "no error" with no values means "not shown". Verified for a field's own condition on 17.6.2 and 18.2.2 only;
        // a condition on its fieldset or page is not covered (docs/spikes.md).
        if (errors.Count > 0 && field.Mandatory && field.Condition is { Enabled: true }
            && !base.ValidateField(form, field, [], context, placeholderParsingService, fieldTypeStorage).Any())
        {
            return [];
        }

        return errors;
    }

    public override IEnumerable<object> ProcessSubmittedValue(Field field, IEnumerable<object> postedValues, HttpContext context)
    {
        var (value, errors) = Evaluate(field, context);

        // Invalid input never reaches storage: ValidateField reports the same errors and Forms refuses the submit.
        return value is null || errors.Count > 0 ? [] : [value.ToJson()];
    }

    // Forms runs ProcessSubmittedValue and then ValidateField on one request (spike S1). Evaluating once keeps the second call
    // from re-scanning the same input, and guarantees both hooks give the same answer.
    private (AddressValue? Value, List<string> Errors) Evaluate(Field field, HttpContext context)
    {
        var (value, errors, _) = EvaluateWithState(field, context);
        return (value, errors);
    }

    private (AddressValue? Value, List<string> Errors, bool NothingPosted) EvaluateWithState(Field field, HttpContext context)
    {
        var key = (typeof(AddressLookupFieldType), field.Id);
        if (context.Items.TryGetValue(key, out var cached) && cached is Outcome known)
        {
            return (known.Value, [.. known.Errors], known.NothingPosted);
        }

        var (value, errors, nothingPosted) = EvaluateCore(field, context);
        context.Items[key] = new Outcome(value, errors, nothingPosted);
        return (value, [.. errors], nothingPosted);
    }

    private sealed record Outcome(AddressValue? Value, IReadOnlyList<string> Errors, bool NothingPosted);

    private (AddressValue? Value, List<string> Errors, bool NothingPosted) EvaluateCore(Field field, HttpContext context)
    {
        var errors = new List<string>();

        // A headless (JSON) submit has no form, and HttpRequest.Form throws on it.
        var form = context.Request.HasFormContentType ? context.Request.Form : FormCollection.Empty;
        var (raw, proof) = AddressFormReader.Read(form, field.Id);
        var mandatory = field.Mandatory;

        if (raw is null)
        {
            if (mandatory)
            {
                errors.AddRange(["Enter the first line of the address", "Enter a town or city", "Enter a postcode"]);
            }

            return (null, errors, true);
        }

        var countries = AddressFieldSettings.ParseCountries(AddressFieldSettings.Get(field.Settings, "Countries"));
        var posted = raw with { CountryIso2 = string.IsNullOrWhiteSpace(raw.CountryIso2) ? countries[0] : raw.CountryIso2 };

        // A cheap ceiling first: the form limit is megabytes per value, and normalising or scanning that on an anonymous
        // endpoint is a memory amplifier. Nothing below runs on input this large.
        if (posted.Lines.Concat([posted.Town, posted.County ?? string.Empty, posted.Postcode]).Any(p => p.Length > HardCeiling))
        {
            return (null, ["A part of the address is too long"], false);
        }

        if (Overlong(posted))
        {
            errors.Add("A part of the address is too long");
        }

        if (HasUnsafeCharacters(posted))
        {
            errors.Add("Remove any special characters from the address");
        }

        var address = posted.Normalise();
        // Line 1 itself, not "any line": a visitor who fills line 2 only would otherwise slip through and shift up.
        if (string.IsNullOrWhiteSpace(posted.Lines[0]))
        {
            errors.Add("Enter the first line of the address");
        }

        // Anything typed means the address has to be whole, mandatory or not: half an address is no use to the editor.
        if (address.Town.Length == 0)
        {
            errors.Add("Enter a town or city");
        }

        if (address.Postcode.Length == 0)
        {
            errors.Add("Enter a postcode");
        }
        else if (!PostcodeLooksRight(address))
        {
            errors.Add("Enter a real postcode");
        }

        if (!countries.Contains(address.CountryIso2, StringComparer.Ordinal))
        {
            errors.Add("Choose a country from the list");
        }

        if (errors.Count > 0)
        {
            return (null, errors, false);
        }

        // The proof covers the address parts and the provider id. The country name is set here from the ISO code, so a
        // posted name can never reach storage.
        var verified = _proofs.Verify(proof, address, field.Id);
        // Only enforceable when a proof can exist: proxy mode with a provider. Capture mode and "no provider" both flag a
        // configuration error instead of making the form unsubmittable.
        var requireLookup = AddressFieldSettings.IsOn(AddressFieldSettings.Get(field.Settings, "RequireLookup"))
            && _options.CurrentValue is { Mode: LookupMode.Proxy, Provider: not null };
        if (requireLookup && !verified)
        {
            errors.Add("Select an address from the list");
            return (null, errors, false);
        }

        var stored = address with
        {
            Country = CountryName(address.CountryIso2),
            Provider = verified ? _providers.FirstOrDefault()?.Alias : null,
            ProviderId = verified ? address.ProviderId : null,
            Source = verified ? "lookup" : "manual",
        };

        return (stored, errors, false);
    }

    private static bool Overlong(AddressValue posted) =>
        posted.Lines.Any(l => l.Trim().Length > MaxLine)
        || posted.Town.Trim().Length > MaxTown
        || (posted.County?.Trim().Length ?? 0) > MaxCounty;

    private static bool PostcodeLooksRight(AddressValue address) =>
        address.CountryIso2 == "GB"
            ? GbPostcode.TryNormalise(address.Postcode, out _)
            : address.Postcode.Length is >= 2 and <= MaxPostcode;

    // Control and format characters (a right-to-left override, a zero-width joiner) have no place in a postal address and are
    // how stored values get spoofed in entry lists and CSV exports. Emoji and every script are allowed.
    private static bool HasUnsafeCharacters(AddressValue posted) =>
        posted.Lines.Concat([posted.Town, posted.County ?? string.Empty, posted.Postcode, posted.ProviderId ?? string.Empty])
            .Any(part => LooksLikeMarkupOrFormula(part) || part.EnumerateRunes().Any(IsUnsafe));

    private static bool IsUnsafe(Rune rune)
    {
        // Joiners and tag characters build emoji sequences (a family, the Welsh flag) and are legitimate in Persian and Indic text.
        if (rune.Value is 0x200C or 0x200D or (>= 0xE0020 and <= 0xE007F))
        {
            return false;
        }

        // These are letters or symbols that render as nothing, so a line made of them passes "not empty" and looks blank in entries.
        if (rune.Value is 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
            or UnicodeCategory.Surrogate;
    }

    // Markup is never part of an address, and a part that starts like a spreadsheet formula is how a stored value attacks the
    // person who opens an export. The Find endpoint already refuses the same characters (design 5.2).
    private static bool LooksLikeMarkupOrFormula(string part)
    {
        if (part.AsSpan().IndexOfAny('<', '>') >= 0)
        {
            return true;
        }

        var trimmed = part.AsSpan().TrimStart();
        return trimmed.Length > 0
            && (trimmed[0] is '=' or '+' or '@'
                || (trimmed[0] == '-' && (trimmed.Length == 1 || !char.IsAsciiDigit(trimmed[1]))));
    }

    private static string? CountryName(string iso2)
    {
        try
        {
            return new RegionInfo(iso2).EnglishName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
