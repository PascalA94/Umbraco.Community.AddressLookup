using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Umbraco.Community.AddressLookup.Security;

/// <summary>What a valid ticket proves: a real form page for this field was rendered recently.</summary>
public readonly record struct LookupTicket(Guid FormId, Guid FieldId, DateTimeOffset Issued);

/// <summary>
/// The lookup ticket embedded in the field view. The proxy refuses any call without a valid one, so the endpoints are
/// not an open relay to the paid provider (design 5.2). The lifetime is checked against <see cref="TimeProvider"/>
/// rather than a time-limited protector so tests can move the clock.
/// </summary>
public sealed class LookupTicketService(IDataProtectionProvider protection, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    // Tolerates small clock differences between load-balanced nodes; anything further ahead is forged or broken.
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(1);

    private const int MaxLength = 1024;

    private readonly IDataProtector _protector = protection.CreateProtector("community.addressLookup.ticket.v1");

    public string Issue(Guid formId, Guid fieldId) =>
        _protector.Protect($"{formId:N}|{fieldId:N}|{time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");

    public bool TryRead(string? ticket, out LookupTicket result)
    {
        result = default;
        if (string.IsNullOrEmpty(ticket) || ticket.Length > MaxLength)
        {
            return false;
        }

        try
        {
            var parts = _protector.Unprotect(ticket).Split('|');
            if (parts.Length != 3
                || !Guid.TryParseExact(parts[0], "N", out var formId)
                || !Guid.TryParseExact(parts[1], "N", out var fieldId)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return false;
            }

            var issued = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var age = time.GetUtcNow() - issued;
            if (age > Lifetime || age < -MaxSkew)
            {
                return false;
            }

            result = new LookupTicket(formId, fieldId, issued);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
