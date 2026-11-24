using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using Umbraco.Community.AddressLookup.Security;

namespace Umbraco.Community.AddressLookup.Tests.Security;

public class TicketTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _protection = DataProtectionProvider.Create("address-lookup-tests");

    private LookupTicketService Create() => new(_protection, _time);

    [Fact]
    public void Ticket_RoundTrip()
    {
        var formId = Guid.NewGuid();
        var fieldId = Guid.NewGuid();
        var service = Create();

        var ticket = service.Issue(formId, fieldId);

        Assert.True(service.TryRead(ticket, out var read));
        Assert.Equal((formId, fieldId, _time.GetUtcNow()), (read.FormId, read.FieldId, read.Issued));
    }

    [Fact]
    public void Ticket_ExpiresAfterTwoHours()
    {
        var service = Create();
        var ticket = service.Issue(Guid.NewGuid(), Guid.NewGuid());

        _time.Advance(TimeSpan.FromHours(2) - TimeSpan.FromSeconds(1));
        Assert.True(service.TryRead(ticket, out _));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(service.TryRead(ticket, out _));
    }

    [Fact]
    public void Ticket_FromTheFuture_False()
    {
        var ahead = new FakeTimeProvider(_time.GetUtcNow().AddHours(1));
        var ticket = new LookupTicketService(_protection, ahead).Issue(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(Create().TryRead(ticket, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("CfDJ8-not-a-real-payload")]
    public void Ticket_Tampered_False(string ticket)
    {
        Assert.False(Create().TryRead(ticket, out _));
    }

    [Fact]
    public void Ticket_OneCharacterChanged_False()
    {
        var service = Create();
        var ticket = service.Issue(Guid.NewGuid(), Guid.NewGuid());
        var tampered = ticket[..^2] + (ticket[^2] == 'A' ? 'B' : 'A') + ticket[^1];

        Assert.False(service.TryRead(tampered, out _));
    }

    [Fact]
    public void Ticket_FromOtherPurpose_False()
    {
        var foreign = _protection.CreateProtector("something.else").Protect("x");

        Assert.False(Create().TryRead(foreign, out _));
    }

    [Fact]
    public void Ticket_Oversize_False()
    {
        Assert.False(Create().TryRead(new string('a', 5000), out _));
    }
}
