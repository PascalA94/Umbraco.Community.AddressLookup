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

    // The country name, the provider alias and the source are display or bookkeeping fields: the browser posts the
    // address parts and the provider id, and those are what a visitor could change.
    private static string Canonical(AddressValue address) =>
        (address.Normalise() with { Country = null, Provider = null, Source = "lookup" }).ToJson();
}
