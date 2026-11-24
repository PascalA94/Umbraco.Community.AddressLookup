using System.Globalization;
using System.Text;

namespace Umbraco.Community.AddressLookup.Controllers;

/// <summary>Input rules applied before any provider call (design 5.2).</summary>
internal static class LookupInput
{
    public const int MinText = 2;
    public const int MaxText = 100;
    public const int MaxId = 200;

    /// <summary>
    /// Maps every Unicode space (a pasted non-breaking space, thin space and so on) to one ASCII space, collapses runs and
    /// trims. Controls are left alone so <see cref="IsValidText"/> still refuses them.
    /// </summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var isSpace = Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator;
            if (isSpace && (builder.Length == 0 || builder[^1] == ' '))
            {
                continue;
            }

            builder.Append(isSpace ? " " : rune.ToString());
        }

        return builder.ToString().TrimEnd(' ');
    }

    /// <summary>
    /// Search text: 2 to 100 characters after trimming, made of letters, marks, digits, spaces and punctuation. Controls,
    /// symbols, format characters and emoji are refused, so a paid provider call is never spent on noise.
    /// </summary>
    public static bool IsValidText(string? text)
    {
        if (text is null || text.Length > MaxText * 2)
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.Length is < MinText or > MaxText)
        {
            return false;
        }

        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (!IsAllowed(rune))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A provider id or container id: 1 to 200 visible ASCII characters (no space, no control).</summary>
    public static bool IsValidId(string? id) =>
        id is { Length: >= 1 and <= MaxId }
        && id.All(c => c is > ' ' and < '\u007f')
        // "." and ".." are dot-segments that a URI would resolve to a sibling provider path.
        && id.Any(c => c != '.');

    private static bool IsAllowed(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark => true,
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        UnicodeCategory.SpaceSeparator => rune.Value == ' ',
        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation => rune.Value != '<' && rune.Value != '>',
        _ => false,
    };
}
