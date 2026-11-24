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
    private const int MaxFieldLines = 3;

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
        // Forms still calls this for a hidden field (it skips only a hidden fieldset on the web path, and nothing at all on the
        // Delivery and Management APIs), and the base implementation is what normally answers "no error" for one. We replace
        // it, so decide visibility ourselves. A field's own condition and its fieldset's are the only levels Forms has: a page
        // has none (only its buttons do). Hidden means no error and nothing stored, mandatory or not, so a visitor who typed an
        // address and then hid the field is not blocked by half an address they can no longer see.
        if (IsHidden(form, field, fieldTypeStorage, placeholderParsingService))
        {
            // ProcessSubmittedValue runs first on the form path and may already have accepted a value.
            context.Items[OutcomeKey(field)] = new Outcome(null, [], true);
            return [];
        }

        var (_, errors, _) = EvaluateWithState(field, context, postedValues);
        return errors;
    }

    public override IEnumerable<object> ProcessSubmittedValue(Field field, IEnumerable<object> postedValues, HttpContext context)
    {
        var (value, errors) = Evaluate(field, context, postedValues);

        return value is null || errors.Count > 0 ? [] : [Serialise(value, field)];
    }

    /// <summary>
    /// Forms stores whatever this returns, and two paths hand it a value nobody validated: a site with a FormPrePopulate handler
    /// (Forms then takes <c>Request.Form[fieldId]</c> as the field's value, and our parts are under other keys) and the headless
    /// Delivery API. So the value stored is never the incoming one when this request already evaluated the field.
    /// </summary>
    public override IEnumerable<object> ConvertToRecord(Field field, IEnumerable<object> postedValues, HttpContext context)
    {
        if (context.Items.TryGetValue(OutcomeKey(field), out var cached) && cached is Outcome known)
        {
            return known.Value is null || known.Errors.Count > 0 ? [] : [Serialise(known.Value, field)];
        }

        // Nothing evaluated this request: the value is one Forms kept in form state (a multi-page form's earlier page), a
        // prefill, or a headless post that skipped validation. It is accepted only if it passes the submit checks again.
        foreach (var candidate in postedValues)
        {
            var (value, errors) = EvaluateSerialised(field, candidate?.ToString());
            if (value is not null && errors.Count == 0)
            {
                return [Serialise(value, field)];
            }
        }

        return [];
    }

    // The tag is what lets a stored lookup survive being read back: a value without a valid one is untrusted, however it
    // claims to have been made. Only a lookup carries one.
    private string Serialise(AddressValue value, Field field) =>
        value.ToJson(value.Source == "lookup" ? _proofs.IssueStoredTag(value, field.Id) : null);

    private static bool IsHidden(Form form, Field field, IFieldTypeStorage fieldTypeStorage, IPlaceholderParsingService placeholders) =>
        form.AllFields.Any(f => f.Id == field.Id) && !IsFieldVisible(form, field, fieldTypeStorage, placeholders);

    private static (Type, Guid) OutcomeKey(Field field) => (typeof(AddressLookupFieldType), field.Id);

    // Forms runs ProcessSubmittedValue and then ValidateField on one request (spike S1). Evaluating once keeps the second call
    // from re-scanning the same input, and guarantees both hooks give the same answer.
    private (AddressValue? Value, List<string> Errors) Evaluate(Field field, HttpContext context, IEnumerable<object> posted)
    {
        var (value, errors, _) = EvaluateWithState(field, context, posted);
        return (value, errors);
    }

    private (AddressValue? Value, List<string> Errors, bool NothingPosted) EvaluateWithState(
        Field field, HttpContext context, IEnumerable<object> posted)
    {
        var key = OutcomeKey(field);
        if (context.Items.TryGetValue(key, out var cached) && cached is Outcome known)
        {
            return (known.Value, [.. known.Errors], known.NothingPosted);
        }

        var (value, errors, nothingPosted) = EvaluateCore(field, context, posted);
        context.Items[key] = new Outcome(value, errors, nothingPosted);
        return (value, [.. errors], nothingPosted);
    }

    private sealed record Outcome(AddressValue? Value, IReadOnlyList<string> Errors, bool NothingPosted);

    private (AddressValue? Value, List<string> Errors, bool NothingPosted) EvaluateCore(
        Field field, HttpContext context, IEnumerable<object> posted)
    {
        var mandatory = field.Mandatory;

        // A headless (JSON) submit has no form, and HttpRequest.Form throws on it. The address then arrives as one JSON value
        // (the stored shape) and is held to the same checks as the parts a browser posts.
        if (!context.Request.HasFormContentType)
        {
            var json = posted.Select(p => p?.ToString()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            if (json is null)
            {
                return (null, mandatory ? MissingParts() : [], true);
            }

            var (value, errors) = EvaluateSerialised(field, json);
            return (value, errors, false);
        }

        var (raw, proof) = AddressFormReader.Read(context.Request.Form, field.Id);
        if (raw is null)
        {
            return (null, mandatory ? MissingParts() : [], true);
        }

        return Finish(field, raw, address => _proofs.Verify(proof, address, field.Id));
    }

    private static List<string> MissingParts() => ["Enter the first line of the address", "Enter a town or city", "Enter a postcode"];

    // A serialised address is the stored shape. Its source is a claim, not evidence: only the keyed tag the submit path put on
    // it proves a provider returned it, so everything else is stored as manual.
    private (AddressValue? Value, List<string> Errors) EvaluateSerialised(Field field, string? json)
    {
        if (!AddressValue.TryParse(json, out var parsed, out var tag) || parsed is null)
        {
            return (null, ["Enter a valid address"]);
        }

        var (value, errors, _) = Finish(field, parsed, address => _proofs.VerifyStoredTag(tag, address, field.Id));
        return (value, errors);
    }

    private (AddressValue? Value, List<string> Errors, bool NothingPosted) Finish(
        Field field, AddressValue raw, Func<AddressValue, bool> isVerified)
    {
        var errors = new List<string>();
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

        // The form posts three lines; a JSON value can carry any number, and a fourth would shift or be silently dropped.
        if (posted.Lines.Count(l => !string.IsNullOrWhiteSpace(l)) > MaxFieldLines)
        {
            errors.Add("Use no more than three address lines");
        }

        var address = posted.Normalise();
        // Line 1 itself, not "any line": a visitor who fills line 2 only would otherwise slip through and shift up.
        if (string.IsNullOrWhiteSpace(posted.Lines.FirstOrDefault()))
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
        var verified = isVerified(address);
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
