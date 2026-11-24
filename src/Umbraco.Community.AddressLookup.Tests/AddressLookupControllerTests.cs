using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Controllers;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Tests.Support;

namespace Umbraco.Community.AddressLookup.Tests;

public sealed class AddressLookupControllerTests : IDisposable
{
    private static readonly Guid FormId = Guid.NewGuid();
    private static readonly Guid FieldId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _protection = DataProtectionProvider.Create("address-lookup-tests");
    private readonly IAddressLookupProvider _provider = Substitute.For<IAddressLookupProvider>();
    private readonly IAddressFieldResolver _fields = Substitute.For<IAddressFieldResolver>();
    private readonly FakeLogger<AddressLookupController> _log = new();
    private readonly AddressLookupOptions _options = new() { Provider = ProviderKind.Stub };
    private readonly List<IDisposable> _disposables = [];

    public AddressLookupControllerTests()
    {
        _fields.Resolve(FormId, FieldId).Returns(new AddressFieldInfo(["GB", "IE"]));
        _provider.FindAsync(Arg.Any<AddressQuery>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 12).Select(i => new AddressSuggestion($"id{i}", $"Text {i}", "Desc", false)).ToList());
        _provider.RetrieveAsync("id1", Arg.Any<CancellationToken>()).Returns(Sample());
    }

    public void Dispose() => _disposables.ForEach(d => d.Dispose());

    private static AddressValue Sample() => new(["1 High Street"], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "stub", "id1", "lookup");

    private string Ticket(Guid? form = null, Guid? field = null) =>
        new LookupTicketService(_protection, _time).Issue(form ?? FormId, field ?? FieldId);

    private AddressLookupController Create(bool withProvider = true)
    {
        var limiter = new LookupRateLimiter(new TestOptionsMonitor<AddressLookupOptions>(_options), _time);
        _disposables.Add(limiter);
        var controller = new AddressLookupController(
            new TestOptionsMonitor<AddressLookupOptions>(_options),
            new LookupTicketService(_protection, _time),
            new AddressProofService(_protection, _time),
            limiter,
            _fields,
            withProvider ? [_provider] : [],
            _log);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static (int Status, string Body) Read(IActionResult result)
    {
        var json = Assert.IsType<JsonResult>(result);
        return (json.StatusCode ?? 200, JsonSerializer.Serialize(json.Value));
    }

    private static string CacheControl(AddressLookupController c) => c.Response.Headers.CacheControl.ToString();

    [Theory]
    [InlineData(nameof(AddressLookupController.Find))]
    [InlineData(nameof(AddressLookupController.Retrieve))]
    public void NoAntiforgery_400(string action)
    {
        var method = typeof(AddressLookupController).GetMethod(action)!;

        Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        Assert.Null(method.GetCustomAttribute<HttpGetAttribute>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-ticket")]
    public async Task BadTicket_400_ProviderNotCalled(string? ticket)
    {
        var controller = Create();

        var find = Read(await controller.Find(ticket, "ST1", null, CancellationToken.None));
        var retrieve = Read(await controller.Retrieve(ticket, "id1", CancellationToken.None));

        Assert.Equal((400, "{\"error\":\"invalid\"}"), find);
        Assert.Equal((400, "{\"error\":\"invalid\"}"), retrieve);
        await _provider.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
        await _provider.DidNotReceiveWithAnyArgs().RetrieveAsync(default!, default);
    }

    [Fact]
    public async Task FieldNotOnForm_400()
    {
        var controller = Create();

        var result = Read(await controller.Find(Ticket(field: Guid.NewGuid()), "ST1", null, CancellationToken.None));

        Assert.Equal((400, "{\"error\":\"invalid\"}"), result);
        await _provider.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
    }

    [Theory]
    [InlineData("a")]
    [InlineData(" a ")]
    [InlineData("\u0000ab")]
    [InlineData("ab\ncd")]
    [InlineData("😀😀")]
    [InlineData("ab😀")]
    [InlineData("<script>")]
    public async Task Find_RejectsControlAndOversize(string text)
    {
        var result = Read(await Create().Find(Ticket(), text, null, CancellationToken.None));

        Assert.Equal((400, "{\"error\":\"invalid\"}"), result);
        await _provider.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
    }

    [Fact]
    public async Task Find_Oversize101Chars_400()
    {
        var result = Read(await Create().Find(Ticket(), new string('a', 101), null, CancellationToken.None));

        Assert.Equal(400, result.Status);
        await _provider.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
    }

    [Theory]
    [InlineData("Ynys Môn")]
    [InlineData("Rhosneigr")]
    [InlineData("St. John's Rd, 10/12 (rear)")]
    [InlineData("LS1 4AP")]
    public async Task Find_AcceptsUnicodeAndPunctuation(string text)
    {
        var result = Read(await Create().Find(Ticket(), text, null, CancellationToken.None));

        Assert.Equal(200, result.Status);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    public async Task DotOnlyIds_400(string id)
    {
        Assert.Equal(400, Read(await Create().Find(Ticket(), "ST1", id, CancellationToken.None)).Status);
        Assert.Equal(400, Read(await Create().Retrieve(Ticket(), id, CancellationToken.None)).Status);
        await _provider.DidNotReceiveWithAnyArgs().RetrieveAsync(default!, default);
    }

    [Fact]
    public async Task Find_NormalisesUnicodeSpaces_BeforeSearching()
    {
        var result = Read(await Create().Find(Ticket(), "LS1\u00A04AP\u2009 ", null, CancellationToken.None));

        Assert.Equal(200, result.Status);
        await _provider.Received(1).FindAsync(Arg.Is<AddressQuery>(q => q.Text == "LS1 4AP"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("\u0000")]
    [InlineData("é")]
    public async Task Find_RejectsBadContainer(string container)
    {
        Assert.Equal(400, Read(await Create().Find(Ticket(), "ST1", container, CancellationToken.None)).Status);
        Assert.Equal(400, Read(await Create().Find(Ticket(), "ST1", new string('a', 201), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Find_UsesServerCountries()
    {
        var controller = Create();
        controller.ControllerContext.HttpContext.Request.Form = new FormCollection(
            new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["countries"] = "US,FR" });

        await controller.Find(Ticket(), "ST1", "c1", CancellationToken.None);

        await _provider.Received(1).FindAsync(
            Arg.Is<AddressQuery>(q => q.Countries.SequenceEqual(new[] { "GB", "IE" }) && q.Container == "c1" && q.Text == "ST1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Find_CapsAtMaxSuggestions()
    {
        _options.MaxSuggestions = 3;

        var result = Read(await Create().Find(Ticket(), "ST1", null, CancellationToken.None));

        Assert.Equal(3, JsonDocument.Parse(result.Body).RootElement.GetArrayLength());
        await _provider.Received(1).FindAsync(Arg.Is<AddressQuery>(q => q.Limit == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Find_ReturnsIdTextDescriptionIsContainer()
    {
        _provider.FindAsync(Arg.Any<AddressQuery>(), Arg.Any<CancellationToken>())
            .Returns([new AddressSuggestion("c1", "LS1 4AP", "12 addresses", true)]);

        var result = Read(await Create().Find(Ticket(), "LS1", null, CancellationToken.None));

        Assert.Equal((200, "[{\"id\":\"c1\",\"text\":\"LS1 4AP\",\"description\":\"12 addresses\",\"isContainer\":true}]"), result);
    }

    [Fact]
    public async Task Throttled_429_RetryAfter()
    {
        _options.RateLimits.FindPerMinute = 1;
        var controller = Create();
        var ticket = Ticket();

        Assert.Equal(200, Read(await controller.Find(ticket, "ST1", null, CancellationToken.None)).Status);
        var second = Read(await controller.Find(ticket, "ST1", null, CancellationToken.None));

        Assert.Equal(429, second.Status);
        Assert.True(int.Parse(controller.Response.Headers.RetryAfter.ToString()) > 0);
        await _provider.Received(1).FindAsync(Arg.Any<AddressQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DailyCap_503_OneWarningPerDay()
    {
        _options.RateLimits.RetrievePerDay = 1;
        _options.RateLimits.RetrievePerMinute = 100;
        var controller = Create();

        Assert.Equal(200, Read(await controller.Retrieve(Ticket(), "id1", CancellationToken.None)).Status);
        var second = Read(await controller.Retrieve(Ticket(), "id1", CancellationToken.None));
        var third = Read(await controller.Retrieve(Ticket(), "id1", CancellationToken.None));

        Assert.Equal((503, "{\"error\":\"unavailable\"}"), second);
        Assert.Equal(503, third.Status);
        Assert.Single(_log.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        await _provider.Received(1).RetrieveAsync("id1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unavailable_503_FixedBody()
    {
        _provider.FindAsync(Arg.Any<AddressQuery>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AddressSuggestion>>(_ => throw new AddressLookupUnavailableException("loqate", "3"));
        _provider.RetrieveAsync("id1", Arg.Any<CancellationToken>())
            .Returns<AddressValue?>(_ => throw new AddressLookupUnavailableException("loqate", "3"));
        var controller = Create();

        Assert.Equal((503, "{\"error\":\"unavailable\"}"), Read(await controller.Find(Ticket(), "ST1", null, CancellationToken.None)));
        Assert.Equal((503, "{\"error\":\"unavailable\"}"), Read(await controller.Retrieve(Ticket(), "id1", CancellationToken.None)));
    }

    [Fact]
    public async Task NoProviderConfigured_503()
    {
        var result = Read(await Create(withProvider: false).Find(Ticket(), "ST1", null, CancellationToken.None));

        Assert.Equal((503, "{\"error\":\"unavailable\"}"), result);
    }

    [Fact]
    public async Task CaptureMode_ProxyRefuses_503()
    {
        _options.Mode = LookupMode.Capture;

        var result = Read(await Create().Find(Ticket(), "ST1", null, CancellationToken.None));

        Assert.Equal(503, result.Status);
        await _provider.DidNotReceiveWithAnyArgs().FindAsync(default!, default);
    }

    [Fact]
    public async Task Responses_NoStore_OnEveryOutcome()
    {
        var checks = new List<(string Name, AddressLookupController Controller)>();

        async Task Run(string name, Func<AddressLookupController, Task<IActionResult>> act, Action? setup = null)
        {
            setup?.Invoke();
            var c = Create();
            await act(c);
            checks.Add((name, c));
        }

        await Run("find 200", c => c.Find(Ticket(), "ST1", null, CancellationToken.None));
        await Run("find 400", c => c.Find("bad", "ST1", null, CancellationToken.None));
        await Run("retrieve 200", c => c.Retrieve(Ticket(), "id1", CancellationToken.None));

        _provider.FindAsync(Arg.Any<AddressQuery>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<AddressSuggestion>>(_ => throw new AddressLookupUnavailableException("stub", "3"));
        await Run("find 503 provider", c => c.Find(Ticket(), "ST1", null, CancellationToken.None));

        Assert.All(checks, c => Assert.Equal("no-store", CacheControl(c.Controller)));
    }

    [Fact]
    public async Task Responses_NoStore_On429AndDailyCap()
    {
        _options.RateLimits.FindPerMinute = 1;
        var throttled = Create();
        var ticket = Ticket();
        await throttled.Find(ticket, "ST1", null, CancellationToken.None);
        throttled.Response.Headers.Clear();

        var result = Read(await throttled.Find(ticket, "ST1", null, CancellationToken.None));

        Assert.Equal(429, result.Status);
        Assert.Equal("no-store", CacheControl(throttled));

        _options.RateLimits.RetrievePerDay = 1;
        _options.RateLimits.RetrievePerMinute = 100;
        var capped = Create();
        await capped.Retrieve(Ticket(), "id1", CancellationToken.None);
        capped.Response.Headers.Clear();

        Assert.Equal(503, Read(await capped.Retrieve(Ticket(), "id1", CancellationToken.None)).Status);
        Assert.Equal("no-store", CacheControl(capped));
    }

    [Fact]
    public async Task Retrieve_ReturnsAddressAndProof()
    {
        var result = Read(await Create().Retrieve(Ticket(), "id1", CancellationToken.None));

        Assert.Equal(200, result.Status);
        var root = JsonDocument.Parse(result.Body).RootElement;
        var address = root.GetProperty("address");
        Assert.Equal("LS1 4AP", address.GetProperty("postcode").GetString());
        Assert.Equal("id1", address.GetProperty("providerId").GetString());
        Assert.Equal("1 High Street", address.GetProperty("lines")[0].GetString());
        Assert.Equal("GB", address.GetProperty("countryIso2").GetString());
        var proof = root.GetProperty("proof").GetString();
        Assert.True(new AddressProofService(_protection, _time).Verify(proof, Sample(), FieldId));
    }

    [Fact]
    public async Task Retrieve_CommercialAddress_FoldedToThreeLines_SoItsProofVerifiesOnSubmit()
    {
        // Providers return up to four lines (company plus three); the field posts three, so the proof must cover the folded form.
        _provider.RetrieveAsync("biz", Arg.Any<CancellationToken>())
            .Returns(Sample() with { Lines = ["Acme Ltd", "Unit 4", "Mill Lane", "Kirkstall"], ProviderId = "biz" });

        var result = Read(await Create().Retrieve(Ticket(), "biz", CancellationToken.None));

        Assert.Equal(200, result.Status);
        var root = JsonDocument.Parse(result.Body).RootElement;
        var lines = root.GetProperty("address").GetProperty("lines").EnumerateArray().Select(l => l.GetString()).ToList();
        Assert.Equal(["Acme Ltd", "Unit 4", "Mill Lane, Kirkstall"], lines);
        var posted = Sample() with { Lines = [.. lines!], ProviderId = "biz" };
        Assert.True(new AddressProofService(_protection, _time).Verify(root.GetProperty("proof").GetString(), posted, FieldId));
    }

    [Fact]
    public async Task Retrieve_UnknownId_400()
    {
        var result = Read(await Create().Retrieve(Ticket(), "missing", CancellationToken.None));

        Assert.Equal((400, "{\"error\":\"invalid\"}"), result);
    }

    [Fact]
    public async Task Retrieve_CountryNotAllowedByField_400()
    {
        _provider.RetrieveAsync("id1", Arg.Any<CancellationToken>()).Returns(Sample() with { CountryIso2 = "FR" });

        var result = Read(await Create().Retrieve(Ticket(), "id1", CancellationToken.None));

        Assert.Equal((400, "{\"error\":\"invalid\"}"), result);
    }

    [Fact]
    public async Task Retrieve_Throttled_429()
    {
        _options.RateLimits.RetrievePerMinute = 1;
        var controller = Create();
        var ticket = Ticket();

        await controller.Retrieve(ticket, "id1", CancellationToken.None);
        var second = Read(await controller.Retrieve(ticket, "id1", CancellationToken.None));

        Assert.Equal(429, second.Status);
        await _provider.Received(1).RetrieveAsync("id1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Responses_NeverEchoInput()
    {
        var bodies = new List<string>
        {
            Read(await Create().Find("SENTINEL-TICKET", "SENTINEL QUERY", null, CancellationToken.None)).Body,
            Read(await Create().Find(Ticket(), "\u0000SENTINEL", "SENTINEL CONTAINER", CancellationToken.None)).Body,
            Read(await Create().Retrieve(Ticket(), "SENTINEL ID", CancellationToken.None)).Body,
        };

        Assert.All(bodies, b => Assert.DoesNotContain("SENTINEL", b));
        Assert.All(_log.Collector.GetSnapshot(), r => Assert.DoesNotContain("SENTINEL", r.Message));
    }
}
