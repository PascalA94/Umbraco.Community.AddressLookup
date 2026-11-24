using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Security;

namespace Umbraco.Community.AddressLookup.Tests.Security;

public class ProofTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _fieldId = Guid.NewGuid();

    private AddressProofService Create() => new(DataProtectionProvider.Create("address-lookup-tests"), _time);

    private static AddressValue Address() => new(
        ["Flat 2", "10 High Street"], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "loqate", "GB|RM|A|1", "lookup");

    [Fact]
    public void Proof_MatchesNormalisedValue()
    {
        var service = Create();
        var proof = service.Issue(Address(), _fieldId);

        var posted = Address() with { Lines = ["  Flat   2", "10 High  Street "], Postcode = "ls14ap", Source = "manual" };

        Assert.True(service.Verify(proof, posted, _fieldId));
    }

    [Fact]
    public void Proof_EditedLine_False()
    {
        var service = Create();
        var proof = service.Issue(Address(), _fieldId);

        Assert.False(service.Verify(proof, Address() with { Lines = ["Flat 2", "12 High Street"] }, _fieldId));
    }

    [Theory]
    [InlineData("town")]
    [InlineData("county")]
    [InlineData("postcode")]
    [InlineData("country")]
    [InlineData("providerId")]
    public void Proof_AnyEditedPart_False(string part)
    {
        var service = Create();
        var proof = service.Issue(Address(), _fieldId);
        var posted = part switch
        {
            "town" => Address() with { Town = "Bradford" },
            "county" => Address() with { County = "Yorkshire" },
            "postcode" => Address() with { Postcode = "LS1 4AQ" },
            "country" => Address() with { CountryIso2 = "IE" },
            _ => Address() with { ProviderId = "GB|RM|A|2" },
        };

        Assert.False(service.Verify(proof, posted, _fieldId));
    }

    [Fact]
    public void Proof_OtherField_False()
    {
        var service = Create();
        var proof = service.Issue(Address(), _fieldId);

        Assert.False(service.Verify(proof, Address(), Guid.NewGuid()));
    }

    [Fact]
    public void Proof_ExpiresAfterTwoHours()
    {
        var service = Create();
        var proof = service.Issue(Address(), _fieldId);

        _time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        Assert.True(service.Verify(proof, Address(), _fieldId));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(service.Verify(proof, Address(), _fieldId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    public void Proof_Garbage_FalseAndDoesNotThrow(string proof)
    {
        Assert.False(Create().Verify(proof, Address(), _fieldId));
    }

    [Fact]
    public void Proof_IsNotAValidTicket()
    {
        var proof = Create().Issue(Address(), _fieldId);

        Assert.False(new LookupTicketService(DataProtectionProvider.Create("address-lookup-tests"), _time).TryRead(proof, out _));
    }
}
