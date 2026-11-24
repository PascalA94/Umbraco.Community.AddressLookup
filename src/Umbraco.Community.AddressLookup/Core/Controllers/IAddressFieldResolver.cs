namespace Umbraco.Community.AddressLookup.Controllers;

/// <summary>What the proxy needs to know about an address lookup field. Always read server-side (design 5.2).</summary>
public sealed record AddressFieldInfo(IReadOnlyList<string> Countries);

/// <summary>
/// Finds an address lookup field on a form, or null when the form or field does not exist or is not this field type.
/// Read-only: the proxy never takes a Forms write service (pitfall P5).
/// </summary>
public interface IAddressFieldResolver
{
    AddressFieldInfo? Resolve(Guid formId, Guid fieldId);
}
