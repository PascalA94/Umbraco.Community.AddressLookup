namespace Umbraco.Community.AddressLookup.Model;

/// <summary>A search sent to a provider. <see cref="Countries"/> always comes from the server-side field setting.</summary>
public sealed record AddressQuery(string Text, string? Container, IReadOnlyList<string> Countries, int Limit)
{
    // No members printed: the text, container and ids are visitor input or address data (design 5.1).
    private bool PrintMembers(System.Text.StringBuilder builder) => false;
}
