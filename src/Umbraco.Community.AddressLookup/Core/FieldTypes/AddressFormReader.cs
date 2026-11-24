using Microsoft.AspNetCore.Http;
using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.FieldTypes;

/// <summary>
/// Reads the field's named inputs from the posted form. Forms only hands a field the value posted under the field id, so
/// <c>{fieldId}.line1</c> and the rest never reach <c>postedValues</c> (spike S1); both submit hooks read the form themselves.
/// </summary>
public static class AddressFormReader
{
    private const int MaxProviderId = 200;
    private const int MaxProof = 4096;

    /// <summary>
    /// The posted parts exactly as sent (not trimmed or normalised, so the length and character checks see the real input),
    /// as a manual address, and the proof if any. The value is null when nothing was typed or picked: the country on its own
    /// does not count, because the view always posts one.
    /// </summary>
    public static (AddressValue? Value, string? Proof) Read(IFormCollection form, Guid fieldId)
    {
        // The first value only: a repeated key would otherwise be joined with commas into one believable-looking part.
        string Part(string name) => form[$"{fieldId}.{name}"].FirstOrDefault() ?? string.Empty;

        var lines = new[] { Part("line1"), Part("line2"), Part("line3") };
        var town = Part("town");
        var county = Part("county");
        var postcode = Part("postcode");

        // Neither is ever legitimately longer, so oversize values are dropped unread (they would only fail verification).
        var providerId = Part("providerId") is { Length: <= MaxProviderId } id ? id : string.Empty;
        var proof = Part("proof") is { Length: <= MaxProof } p ? p : string.Empty;

        var anyInput = lines.Concat([town, county, postcode]).Any(p => !string.IsNullOrWhiteSpace(p));
        if (!anyInput)
        {
            return (null, null);
        }

        var value = new AddressValue(
            lines, town, string.IsNullOrWhiteSpace(county) ? null : county, postcode,
            Part("country"), null, null, string.IsNullOrWhiteSpace(providerId) ? null : providerId, "manual");

        return (value, string.IsNullOrEmpty(proof) ? null : proof);
    }
}
