using System.Net;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Providers.Loqate;
using Umbraco.Community.AddressLookup.Tests.Support;

namespace Umbraco.Community.AddressLookup.Tests.Providers;

public class LoqateProviderTests
{
    private const string Key = "lq-SENTINEL-KEY"; // gitleaks:allow

    private readonly FakeTimeProvider _time = new();
    private readonly FakeLogger<LoqateProvider> _log = new();

    private LoqateProvider Create(RecordingHandler handler, int timeoutSeconds = 5)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(LoqateProvider.ClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.addressy.com/") });
        var options = new AddressLookupOptions { Provider = ProviderKind.Loqate, TimeoutSeconds = timeoutSeconds, Loqate = { ApiKey = Key } };
        return new LoqateProvider(factory, new TestOptionsMonitor<AddressLookupOptions>(options), _time, _log);
    }

    private static AddressQuery Query(string text = "LS1", string? container = null) => new(text, container, ["GB", "IE"], 8);

    private const string FindOk =
        "{\"Items\":[{\"Id\":\"GB|RM|A|1\",\"Type\":\"Address\",\"Text\":\"1 High St\",\"Highlight\":\"0-2\",\"Description\":\"Leeds, LS1 4AP\"}]}";

    [Fact]
    public async Task Find_PostsFormBody()
    {
        var handler = RecordingHandler.Json(FindOk);

        await Create(handler).FindAsync(Query("LS1 4"), CancellationToken.None);
        await Create(handler).FindAsync(Query("LS1 4", "c|1"), CancellationToken.None);

        var first = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, first.Method);
        Assert.Equal("/Capture/Interactive/Find/v1.20/json6.ws", first.Uri.AbsolutePath);
        Assert.Equal(string.Empty, first.Uri.Query);
        Assert.Equal(Key, first.Form["Key"]);
        Assert.Equal("LS1 4", first.Form["Text"]);
        Assert.Equal("GB,IE", first.Form["Countries"]);
        Assert.Equal("8", first.Form["Limit"]);
        Assert.Equal("true", first.Form["IsMiddleware"]);
        Assert.False(first.Form.ContainsKey("Container"));
        Assert.Equal("c|1", handler.Requests[1].Form["Container"]);
    }

    [Theory]
    [InlineData("Address", false)]
    [InlineData("Postcode", true)]
    [InlineData("Street", true)]
    [InlineData("Container", true)]
    public async Task Find_MapsTypeToIsContainer(string type, bool isContainer)
    {
        var json = $"{{\"Items\":[{{\"Id\":\"x\",\"Type\":\"{type}\",\"Text\":\"t\",\"Description\":\"d\"}}]}}";

        var results = await Create(RecordingHandler.Json(json)).FindAsync(Query(), CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(isContainer, item.IsContainer);
        Assert.Equal(("x", "t", "d"), (item.Id, item.Text, item.Description));
    }

    [Fact]
    public async Task Retrieve_MapsFields()
    {
        const string json =
            "{\"Items\":[{\"Company\":\"Acme Ltd\",\"Line1\":\"1 High Street\",\"Line2\":\"Ynys Môn Park\",\"Line3\":\"\",\"City\":\"Leeds\"," +
            "\"Province\":\"West Yorkshire\",\"PostalCode\":\"LS1 4AP\",\"CountryIso2\":\"GB\",\"CountryName\":\"United Kingdom\"}]}";
        var handler = RecordingHandler.Json(json);

        var address = await Create(handler).RetrieveAsync("GB|RM|A|1", CancellationToken.None);

        Assert.NotNull(address);
        Assert.Equal(["Acme Ltd", "1 High Street", "Ynys Môn Park"], address.Lines);
        Assert.Equal(("Leeds", "West Yorkshire", "LS1 4AP", "GB", "United Kingdom"),
            (address.Town, address.County, address.Postcode, address.CountryIso2, address.Country));
        Assert.Equal(("loqate", "GB|RM|A|1", "lookup"), (address.Provider, address.ProviderId, address.Source));
        var request = handler.Requests.Single();
        Assert.Equal("/Capture/Interactive/Retrieve/v1.30/json6.ws", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal(Key, request.Form["Key"]);
        Assert.Equal("GB|RM|A|1", request.Form["Id"]);
    }

    [Fact]
    public async Task Retrieve_NoItems_Null()
    {
        Assert.Null(await Create(RecordingHandler.Json("{\"Items\":[]}")).RetrieveAsync("x", CancellationToken.None));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("8")]
    [InlineData("10")]
    [InlineData("14")]
    [InlineData("17")]
    [InlineData("-1")]
    [InlineData("1001")]
    public async Task Errors_MapToUnavailable(string code)
    {
        var json = $"{{\"Items\":[{{\"Error\":\"{code}\",\"Description\":\"Unknown key lq-SENTINEL-KEY\",\"Cause\":\"c\",\"Resolution\":\"r\"}}]}}";
        var provider = Create(RecordingHandler.Json(json));

        var find = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query(), CancellationToken.None));
        var retrieve = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.RetrieveAsync("x", CancellationToken.None));

        Assert.All(new[] { find, retrieve }, ex =>
        {
            Assert.Equal(("loqate", code), (ex.Provider, ex.Code));
            Assert.Null(ex.InnerException);
            Assert.DoesNotContain("SENTINEL", ex.Message);
        });
    }

    [Fact]
    public async Task Errors_NumericCode_AreMapped()
    {
        var provider = Create(RecordingHandler.Json("{\"Items\":[{\"Error\":2,\"Description\":\"x\"}]}"));

        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query(), CancellationToken.None));

        Assert.Equal("2", ex.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "HTTP-500")]
    [InlineData(HttpStatusCode.Found, "HTTP-302")]
    public async Task NonSuccessStatus_MapsToUnavailable(HttpStatusCode status, string code)
    {
        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => Create(RecordingHandler.Status(status)).FindAsync(Query(), CancellationToken.None));

        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task MalformedBody_MapsToUnavailable()
    {
        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => Create(RecordingHandler.Json("<html>")).FindAsync(Query(), CancellationToken.None));

        Assert.Equal("BAD-RESPONSE", ex.Code);
    }

    [Fact]
    public async Task OversizeBody_MapsToUnavailable()
    {
        var huge = "{\"Items\":[],\"Pad\":\"" + new string('a', 70 * 1024) + "\"}";

        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => Create(RecordingHandler.Json(huge)).FindAsync(Query(), CancellationToken.None));

        Assert.Equal("TOO-LARGE", ex.Code);
    }

    [Fact]
    public async Task Errors_LogThrottled()
    {
        var provider = Create(RecordingHandler.Json("{\"Items\":[{\"Error\":\"3\"}]}"));

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query(), CancellationToken.None));
        }

        var warning = Assert.Single(_log.Collector.GetSnapshot(), r => r.Level == LogLevel.Warning);
        Assert.Contains("loqate", warning.Message);
        Assert.Contains("3", warning.Message);

        _time.Advance(TimeSpan.FromSeconds(61));
        await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query(), CancellationToken.None));
        Assert.Equal(2, _log.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task Errors_LogNeverCarriesKeyQueryOrDescription()
    {
        var provider = Create(RecordingHandler.Json("{\"Items\":[{\"Error\":\"2\",\"Description\":\"bad lq-SENTINEL-KEY\"}]}"));

        await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query("SENTINEL QUERY"), CancellationToken.None));

        Assert.All(_log.Collector.GetSnapshot(), r => Assert.DoesNotContain("SENTINEL", r.Message));
    }

    [Fact]
    public async Task Timeout_ThrowsUnavailable()
    {
        var started = new TaskCompletionSource();
        var handler = new RecordingHandler(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var pending = Create(handler, timeoutSeconds: 5).FindAsync(Query(), CancellationToken.None);

        await started.Task;
        _time.Advance(TimeSpan.FromSeconds(6));

        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => pending);
        Assert.Equal("TIMEOUT", ex.Code);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task CallerCancellation_IsNotReportedAsUnavailable()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var pending = Create(handler).FindAsync(Query(), cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public void Redirect_NotFollowed()
    {
        using var handler = ProviderHttp.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Find_SkipsItemsWithoutIdOrText()
    {
        const string json = "{\"Items\":[{\"Foo\":\"bar\"},{\"Id\":\"\",\"Text\":\"x\"},{\"Id\":\"ok\",\"Type\":\"Address\",\"Text\":\"1 High St\"}]}";

        var results = await Create(RecordingHandler.Json(json)).FindAsync(Query(), CancellationToken.None);

        Assert.Equal("ok", Assert.Single(results).Id);
    }

    [Fact]
    public async Task Find_AllItemsUnreadable_IsBadResponse()
    {
        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => Create(RecordingHandler.Json("{\"Items\":[{\"Foo\":\"bar\"}]}")).FindAsync(Query(), CancellationToken.None));

        Assert.Equal("BAD-RESPONSE", ex.Code);
    }
}
