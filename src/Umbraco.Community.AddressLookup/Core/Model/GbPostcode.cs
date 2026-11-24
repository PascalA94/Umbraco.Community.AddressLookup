using System.Text.RegularExpressions;

namespace Umbraco.Community.AddressLookup.Model;

/// <summary>GB postcode shape check and canonical formatting (outward code, one space, three-character inward code).</summary>
internal static partial class GbPostcode
{
    // The gov.uk bulk-validation pattern, applied to the postcode with spaces removed.
    [GeneratedRegex(
        @"^(GIR0AA|[A-PR-UWYZ]([0-9]{1,2}|([A-HK-Y][0-9]([0-9ABEHMNPRV-Y])?)|[0-9][A-HJKPS-UW])[0-9][ABD-HJLNP-UW-Z]{2})$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Shape();

    public static bool TryNormalise(string? input, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var compact = string.Concat(input.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        try
        {
            if (!Shape().IsMatch(compact))
            {
                return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Its Input property holds the postcode, so the exception is never propagated or logged.
            return false;
        }

        normalised = $"{compact[..^3]} {compact[^3..]}";
        return true;
    }
}
