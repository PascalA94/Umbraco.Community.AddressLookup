namespace Umbraco.Community.AddressLookup.Configuration;

/// <summary>The lookup provider behind the field. Unset means manual entry only.</summary>
public enum ProviderKind
{
    Loqate,
    IdealPostcodes,
    Stub,
}
