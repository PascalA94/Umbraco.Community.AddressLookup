using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;

namespace Umbraco.Community.AddressLookup.Tests;

public class StubProviderTests
{
    private static readonly StubProvider Provider = new();

    private static AddressQuery Query(string text, string? container = null, int limit = 8) =>
        new(text, container, ["GB"], limit);

    [Fact]
    public async Task Find_ST1_ReturnsContainerAndAddresses()
    {
        var results = await Provider.FindAsync(Query("ST1"), CancellationToken.None);

        Assert.Contains(results, r => r.IsContainer);
        Assert.Contains(results, r => !r.IsContainer);
    }

    [Theory]
    [InlineData("st11aa")]
    [InlineData("  ST1 1AA ")]
    public async Task Find_IsCaseAndSpaceInsensitive(string text)
    {
        Assert.NotEmpty(await Provider.FindAsync(Query(text), CancellationToken.None));
    }

    [Fact]
    public async Task Find_Container_DrillsIn()
    {
        var container = (await Provider.FindAsync(Query("ST1"), CancellationToken.None)).First(r => r.IsContainer);

        var inside = await Provider.FindAsync(Query("ST1", container.Id), CancellationToken.None);

        Assert.NotEmpty(inside);
        Assert.All(inside, r => Assert.False(r.IsContainer));
    }

    [Fact]
    public async Task Find_RespectsLimit()
    {
        Assert.Single(await Provider.FindAsync(Query("ST1", limit: 1), CancellationToken.None));
    }

    [Fact]
    public async Task Find_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(await Provider.FindAsync(Query("QQ9 9QQ"), CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_KnownId_ReturnsAddress()
    {
        var hit = (await Provider.FindAsync(Query("ST1"), CancellationToken.None)).First(r => !r.IsContainer);

        var address = await Provider.RetrieveAsync(hit.Id, CancellationToken.None);

        Assert.NotNull(address);
        Assert.Equal("stub", address.Provider);
        Assert.Equal(hit.Id, address.ProviderId);
        Assert.Equal("GB", address.CountryIso2);
        Assert.StartsWith("ST1", address.Postcode);
    }

    [Fact]
    public async Task Retrieve_UnknownId_Null()
    {
        Assert.Null(await Provider.RetrieveAsync("nope", CancellationToken.None));
    }

    [Fact]
    public async Task Find_UNAVAILABLE_Throws()
    {
        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => Provider.FindAsync(Query("ZZ99 9ZZ"), CancellationToken.None));

        Assert.Equal("stub", ex.Provider);
        Assert.Equal("UNAVAILABLE", ex.Code);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("ZZ99", ex.Message);
    }
}
