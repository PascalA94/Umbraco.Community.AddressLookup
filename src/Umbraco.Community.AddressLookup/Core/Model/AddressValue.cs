using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace Umbraco.Community.AddressLookup.Model;

/// <summary>
/// One postal address as stored in a Forms record (design 3.3). <see cref="Source"/> is <c>lookup</c> only when a
/// proof of lookup verified; otherwise <c>manual</c>.
/// </summary>
public sealed record AddressValue(
    IReadOnlyList<string> Lines,
    string Town,
    string? County,
    string Postcode,
    string CountryIso2,
    string? Country,
    string? Provider,
    string? ProviderId,
    string Source)
{
    /// <summary>Stored JSON schema version (key <c>v</c>).</summary>
    public const int Version = 1;

    private const int MaxJsonLength = 4096;
    private const int MaxFieldLines = 3;
    private const int MaxLine = 100;
    private const int MaxTown = 60;
    private const int MaxCounty = 60;

    // Non-ASCII stays readable in entries and CSV exports (spike S1), but <, >, &, ' and + stay escaped because the
    // default Forms email workflow and some entry views render the raw string.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(new TextEncoderSettings(UnicodeRanges.All)),
        WriteIndented = false,
    };

    private static readonly AddressJsonContext Context = new(JsonOptions);

    // The compiler-generated ToString prints every member. An address is personal data and must never reach a log line or
    // exception text through a "{Value}" argument or a debugger dump (design 5.1).
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("v=").Append(Version).Append(", source=").Append(Source);
        return true;
    }

    public string ToJson() => ToJson(null);

    /// <summary>
    /// The stored JSON with an optional keyed tag (key <c>t</c>) saying the server itself issued this lookup. Readers that do
    /// not know the key ignore it, so the schema version stays 1; a value without a valid tag is untrusted.
    /// </summary>
    public string ToJson(string? tag) => JsonSerializer.Serialize(
        new Wire(Version, [.. Lines], Town, County, Postcode, CountryIso2, Country, Provider, ProviderId, Source, tag),
        Context.Wire);

    /// <summary>Parses stored JSON. Never throws: malformed, oversize or unknown-version input returns false.</summary>
    public static bool TryParse(string? json, out AddressValue? value) => TryParse(json, out value, out _);

    /// <summary>As <see cref="TryParse(string?, out AddressValue?)"/>, also returning the keyed tag, or null when there is none.</summary>
    public static bool TryParse(string? json, out AddressValue? value, out string? tag)
    {
        value = null;
        tag = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonLength)
        {
            return false;
        }

        try
        {
            var wire = JsonSerializer.Deserialize(json, Context.Wire);
            if (wire is not { V: Version, Lines: not null, Town: not null, Postcode: not null, CountryIso2: not null, Source: not null })
            {
                return false;
            }

            value = new AddressValue(
                wire.Lines, wire.Town, wire.County, wire.Postcode, wire.CountryIso2, wire.Country, wire.Provider, wire.ProviderId, wire.Source);
            tag = wire.T;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Trims and collapses whitespace, drops empty lines, and upper-cases and spaces a GB postcode. Proof checks and
    /// storage both go through this, so cosmetic differences in what a browser posts never break a proof.
    /// </summary>
    public AddressValue Normalise()
    {
        var country = Clean(CountryIso2).ToUpperInvariant();
        var postcode = Clean(Postcode);
        if (country == "GB")
        {
            postcode = GbPostcode.TryNormalise(postcode, out var gb) ? gb : postcode.ToUpperInvariant();
        }

        return this with
        {
            Lines = [.. Lines.Select(Clean).Where(l => l.Length > 0)],
            Town = Clean(Town),
            County = NullIfEmpty(County),
            Postcode = postcode,
            CountryIso2 = country,
            Country = NullIfEmpty(Country),
        };
    }

    /// <summary>
    /// Shapes a provider's address to what the field can post and store: normalised, at most three lines (providers add a
    /// company line, so overflow folds into line 3), and within the submit length caps. Retrieve issues its proof over this
    /// shape, so a picked address verifies at submit instead of silently becoming <c>manual</c>.
    /// </summary>
    public AddressValue FitToField()
    {
        var address = Normalise();
        var lines = address.Lines.Count > MaxFieldLines
            ? [.. address.Lines.Take(MaxFieldLines - 1), string.Join(", ", address.Lines.Skip(MaxFieldLines - 1))]
            : address.Lines.ToList();

        return address with
        {
            Lines = [.. lines.Select(l => Truncate(l, MaxLine))],
            Town = Truncate(address.Town, MaxTown),
            County = address.County is null ? null : Truncate(address.County, MaxCounty),
        };
    }

    /// <summary>A one-line form for placeholders, the preview and the README.</summary>
    public string Format() => string.Join(
        ", ",
        new[] { string.Join(", ", Lines), Town, County ?? string.Empty, Postcode, Country ?? string.Empty }
            .Where(p => p.Length > 0));

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        // Do not cut a surrogate pair in half.
        var length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..length].TrimEnd();
    }

    private static string Clean(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? NullIfEmpty(string? text)
    {
        var cleaned = Clean(text);
        return cleaned.Length == 0 ? null : cleaned;
    }

    internal sealed record Wire(
        [property: JsonPropertyName("v"), JsonPropertyOrder(0)] int V,
        [property: JsonPropertyName("lines"), JsonPropertyOrder(1)] List<string>? Lines,
        [property: JsonPropertyName("town"), JsonPropertyOrder(2)] string? Town,
        [property: JsonPropertyName("county"), JsonPropertyOrder(3)] string? County,
        [property: JsonPropertyName("postcode"), JsonPropertyOrder(4)] string? Postcode,
        [property: JsonPropertyName("countryIso2"), JsonPropertyOrder(5)] string? CountryIso2,
        [property: JsonPropertyName("country"), JsonPropertyOrder(6)] string? Country,
        [property: JsonPropertyName("provider"), JsonPropertyOrder(7)] string? Provider,
        [property: JsonPropertyName("providerId"), JsonPropertyOrder(8)] string? ProviderId,
        [property: JsonPropertyName("source"), JsonPropertyOrder(9)] string? Source,
        [property: JsonPropertyName("t"), JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? T = null)
    {
        // Same rule as the public record: the compiler's ToString would print every address part.
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append("v=").Append(V);
            return true;
        }
    }
}

[JsonSerializable(typeof(AddressValue.Wire))]
internal sealed partial class AddressJsonContext : JsonSerializerContext;
