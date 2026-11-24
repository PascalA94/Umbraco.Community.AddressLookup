namespace Umbraco.Community.AddressLookup.Model;

/// <summary>One search result. A container drills in with another Find; an address is retrieved.</summary>
public sealed record AddressSuggestion(string Id, string Text, string? Description, bool IsContainer)
{
    // No members printed: the text, container and ids are visitor input or address data (design 5.1).
    private bool PrintMembers(System.Text.StringBuilder builder) => false;
}
