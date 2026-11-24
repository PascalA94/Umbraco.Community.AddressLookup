using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.Security;

/// <summary>
/// Proof that an address came from a provider lookup (design 5.3). Retrieve issues a proof over the normalised address
/// and the field; submit re-normalises what the browser posted and compares. A matching proof means the visitor did not
/// edit the result, with no second paid Retrieve. A page open for longer than the lifetime simply falls back to manual.
/// </summary>
public sealed class AddressProofService(IDataProtectionProvider protection, TimeProvider time)
{
    private const int MaxLength = 4096;

    private readonly IDataProtector _protector = protection.CreateProtector("community.addressLookup.proof.v1");

    // A separate purpose, so a proof (which the browser holds) can never be presented as a stored-value tag or the reverse.
    private readonly IDataProtector _tagProtector = protection.CreateProtector("community.addressLookup.storedTag.v1");

    public string Issue(AddressValue address, Guid fieldId) =>
        _protector.Protect(
            $"{fieldId:N}|{time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}|{Canonical(address)}");

    public bool Verify(string? proof, AddressValue posted, Guid fieldId)
    {
        if (string.IsNullOrEmpty(proof) || proof.Length > MaxLength)
        {
            return false;
        }

        try
        {
            // The canonical JSON can contain '|', so split into three at most.
            var parts = _protector.Unprotect(proof).Split('|', 3);
            if (parts.Length != 3
                || !Guid.TryParseExact(parts[0], "N", out var proofField)
                || proofField != fieldId
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            var age = time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (age > LookupTicketService.Lifetime || age < TimeSpan.FromMinutes(-1))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(parts[2]),
                System.Text.Encoding.UTF8.GetBytes(Canonical(posted)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// A keyed tag stored inside a lookup value, saying this server accepted it as a verified lookup. It is how a stored value
    /// proves it was made by the submit path and not typed into a prefill, a form state or a headless post. It binds the
    /// field and a digest of the address (so the address itself is not repeated), and has no expiry: a multi-page form or a
    /// later re-render must still trust it. That makes it as replayable as the proof, and a data-quality control in the same way.
    /// </summary>
    public string IssueStoredTag(AddressValue address, Guid fieldId) =>
        _tagProtector.Protect($"{fieldId:N}|{Digest(address)}");

    public bool VerifyStoredTag(string? tag, AddressValue stored, Guid fieldId)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > MaxLength || stored.Source != "lookup" || string.IsNullOrEmpty(stored.ProviderId))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(_tagProtector.Unprotect(tag)),
                System.Text.Encoding.UTF8.GetBytes($"{fieldId:N}|{Digest(stored)}"));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string Digest(AddressValue address) =>
        Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Canonical(address))));

    // The country name, the provider alias and the source are display or bookkeeping fields: the browser posts the
    // address parts and the provider id, and those are what a visitor could change.
    private static string Canonical(AddressValue address) =>
        (address.Normalise() with { Country = null, Provider = null, Source = "lookup" }).ToJson();
}
