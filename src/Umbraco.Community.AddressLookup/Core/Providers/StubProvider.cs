using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.Providers;

/// <summary>
/// A fixed, fictional data set for tests, demos and CI. Development only (the options validator refuses it elsewhere).
/// <c>ST1</c> returns a container and two addresses; <c>ZZ99 9ZZ</c> simulates an outage so the fallback UI can be tested.
/// </summary>
public sealed class StubProvider : IAddressLookupProvider
{
    private const string ContainerId = "stub-c1";

    private static readonly IReadOnlyDictionary<string, AddressValue> Addresses = new Dictionary<string, AddressValue>
    {
        ["stub-a1"] = Make("stub-a1", "Flat 1", "1 Example Street", "ST1 1AA"),
        ["stub-a2"] = Make("stub-a2", "Flat 2", "1 Example Street", "ST1 1AA"),
        ["stub-a3"] = Make("stub-a3", "Flat 3", "1 Example Street", "ST1 1AA"),
        ["stub-a4"] = Make("stub-a4", null, "2 Example Street", "ST1 1AB"),
        ["stub-a5"] = Make("stub-a5", null, "3 Example Street", "ST1 1AB"),
    };

    private static readonly string[] ContainerMembers = ["stub-a1", "stub-a2", "stub-a3"];

    public string Alias => "stub";

    public Task<IReadOnlyList<AddressSuggestion>> FindAsync(AddressQuery query, CancellationToken ct)
    {
        var compact = Compact(query.Text);
        if (compact == "ZZ999ZZ")
        {
            throw new AddressLookupUnavailableException(Alias, "UNAVAILABLE");
        }

        IEnumerable<AddressSuggestion> hits;
        if (query.Container is not null)
        {
            hits = query.Container == ContainerId ? ContainerMembers.Select(Suggest) : [];
        }
        else if (compact.StartsWith("ST1", StringComparison.Ordinal))
        {
            hits = [new AddressSuggestion(ContainerId, "ST1 1AA", "3 addresses", true), Suggest("stub-a4"), Suggest("stub-a5")];
        }
        else
        {
            hits = [];
        }

        return Task.FromResult<IReadOnlyList<AddressSuggestion>>([.. hits.Take(Math.Max(0, query.Limit))]);
    }

    public Task<AddressValue?> RetrieveAsync(string id, CancellationToken ct) =>
        Task.FromResult(Addresses.TryGetValue(id, out var address) ? address : null);

    private static AddressSuggestion Suggest(string id)
    {
        var a = Addresses[id];
        return new AddressSuggestion(id, string.Join(", ", a.Lines), $"{a.Town}, {a.Postcode}", false);
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    private static AddressValue Make(string id, string? flat, string street, string postcode) => new(
        flat is null ? [street] : [flat, street], "Teststone", "Teststaffs", postcode, "GB", "United Kingdom", "stub", id, "lookup");
}
