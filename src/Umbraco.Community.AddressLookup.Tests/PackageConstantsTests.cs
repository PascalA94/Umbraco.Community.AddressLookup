using Umbraco.Community.AddressLookup.Web;

namespace Umbraco.Community.AddressLookup.Tests;

public class PackageConstantsTests
{
    [Fact]
    public void Package_name_matches_the_assembly()
        => Assert.Equal(PackageConstants.PackageName, typeof(PackageConstants).Assembly.GetName().Name);

    [Fact]
    public void Composer_can_be_constructed()
        => Assert.NotNull(new AddressLookupComposer());
}
