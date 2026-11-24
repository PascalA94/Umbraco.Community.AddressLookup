namespace Umbraco.Community.AddressLookup.FieldTypes;

/// <summary>
/// The field's own settings (design 3.2), parsed in one place so the field type, the lookup proxy's resolver and the
/// view agree on what <c>Countries</c> and the toggles mean.
/// </summary>
public static class AddressFieldSettings
{
    public const string DefaultCountry = "GB";

    /// <summary>
    /// A comma or space separated list of ISO 3166 alpha-2 codes, upper-cased and de-duplicated. Anything that is not two
    /// letters is dropped, and an empty result means <c>GB</c>, so a half-configured field never searches the world.
    /// </summary>
    public static IReadOnlyList<string> ParseCountries(string? setting)
    {
        var countries = (setting ?? string.Empty)
            .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length == 2 && c.All(char.IsAsciiLetter))
            .Select(c => c.ToUpperInvariant())
            .Distinct()
            .ToList();

        return countries.Count == 0 ? [DefaultCountry] : countries;
    }

    /// <summary>Forms stores Toggle settings as the strings <c>true</c> and <c>false</c> (or <c>1</c> and <c>0</c>).</summary>
    public static bool IsOn(string? setting, bool whenMissing = false) =>
        string.IsNullOrWhiteSpace(setting)
            ? whenMissing
            : setting.Trim() is "1" || setting.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    public static string? Get(IDictionary<string, string>? settings, string key) =>
        settings is not null && settings.TryGetValue(key, out var value) ? value : null;
}
