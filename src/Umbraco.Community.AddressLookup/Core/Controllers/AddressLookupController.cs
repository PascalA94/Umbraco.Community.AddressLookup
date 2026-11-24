using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;

namespace Umbraco.Community.AddressLookup.Controllers;

/// <summary>
/// The server-side lookup proxy (design 3.4, 5.2). Every action is POST-only with an antiforgery token, needs a valid
/// lookup ticket, is throttled and answers with fixed payloads, so it cannot be used as an open relay to the paid provider.
/// </summary>
/// <remarks>
/// A plain controller on the surface URL rather than a <c>SurfaceController</c>: it needs none of the CMS context a
/// surface controller injects, which keeps it free of Umbraco-line differences and testable without the CMS.
/// </remarks>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("umbraco/surface/AddressLookup")]
public sealed class AddressLookupController(
    IOptionsMonitor<AddressLookupOptions> options,
    LookupTicketService tickets,
    AddressProofService proofs,
    LookupRateLimiter limiter,
    IAddressFieldResolver fields,
    IEnumerable<IAddressLookupProvider> providers,
    ILogger<AddressLookupController> logger) : ControllerBase
{
    [HttpPost("Find")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Find(
        [FromForm] string? ticket, [FromForm] string? text, [FromForm] string? container, CancellationToken ct)
    {
        NoStore();
        if (Authorise(ticket) is not var (_, fieldId, field) || field is null)
        {
            return Invalid();
        }

        text = LookupInput.Normalise(text);
        if (!LookupInput.IsValidText(text) || (container is not null && !LookupInput.IsValidId(container)))
        {
            return Invalid();
        }

        var settings = options.CurrentValue;
        if (Provider(settings) is not { } provider)
        {
            return Unavailable();
        }

        if (Throttle(LookupKind.Find, ticket!) is { } refused)
        {
            return refused;
        }

        try
        {
            var query = new AddressQuery(text!.Trim(), container, field.Countries, settings.MaxSuggestions);
            var hits = await provider.FindAsync(query, ct);
            return Json(200, hits.Take(settings.MaxSuggestions)
                .Select(h => new { id = h.Id, text = h.Text, description = h.Description, isContainer = h.IsContainer })
                .ToList());
        }
        catch (AddressLookupUnavailableException)
        {
            // The provider already logged its code, throttled. The visitor gets the fixed payload and falls back to manual entry.
            return Unavailable();
        }
    }

    [HttpPost("Retrieve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retrieve([FromForm] string? ticket, [FromForm] string? id, CancellationToken ct)
    {
        NoStore();
        if (Authorise(ticket) is not var (_, fieldId, field) || field is null || !LookupInput.IsValidId(id))
        {
            return Invalid();
        }

        var settings = options.CurrentValue;
        if (Provider(settings) is not { } provider)
        {
            return Unavailable();
        }

        if (Throttle(LookupKind.Retrieve, ticket!) is { } refused)
        {
            return refused;
        }

        try
        {
            var address = (await provider.RetrieveAsync(id!, ct))?.FitToField();

            // A provider can return a country the field does not allow; the same rule applies at submit.
            if (address is null || !field.Countries.Contains(address.CountryIso2, StringComparer.OrdinalIgnoreCase))
            {
                return Invalid();
            }

            return Json(200, new
            {
                address = new
                {
                    lines = address.Lines,
                    town = address.Town,
                    county = address.County,
                    postcode = address.Postcode,
                    countryIso2 = address.CountryIso2,
                    country = address.Country,
                    providerId = address.ProviderId,
                },
                proof = proofs.Issue(address, fieldId),
            });
        }
        catch (AddressLookupUnavailableException)
        {
            return Unavailable();
        }
    }

    private (Guid FormId, Guid FieldId, AddressFieldInfo? Field)? Authorise(string? ticket)
    {
        if (!tickets.TryRead(ticket, out var read))
        {
            return null;
        }

        // The ticket proves a form page was rendered, not that the field still exists or is ours: check on every call,
        // and take the countries from the field, never from the request.
        return (read.FormId, read.FieldId, fields.Resolve(read.FormId, read.FieldId));
    }

    private IAddressLookupProvider? Provider(AddressLookupOptions settings) =>
        settings.Mode == LookupMode.Proxy ? providers.FirstOrDefault() : null;

    private IActionResult? Throttle(LookupKind kind, string ticket)
    {
        var decision = limiter.TryAcquire(kind, ticket, HttpContext.Connection.RemoteIpAddress?.ToString());
        if (decision.Allowed)
        {
            return null;
        }

        if (decision.DailyCapHit)
        {
            if (decision.FirstDailyCapHit)
            {
                logger.LogWarning("Address lookup daily {Kind} cap reached; refusing those lookups until UTC midnight", kind);
            }

            return Unavailable();
        }

        var seconds = (int)Math.Ceiling((decision.RetryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds);
        Response.Headers.RetryAfter = Math.Max(1, seconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Json(429, new { error = "throttled" });
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private static JsonResult Json(int status, object body) => new(body) { StatusCode = status };

    private static JsonResult Invalid() => Json(400, new { error = "invalid" });

    private static JsonResult Unavailable() => Json(503, new { error = "unavailable" });
}
