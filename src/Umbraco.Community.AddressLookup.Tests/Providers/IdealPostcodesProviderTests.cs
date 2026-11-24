using System.Net;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Providers.IdealPostcodes;
using Umbraco.Community.AddressLookup.Tests.Support;

namespace Umbraco.Community.AddressLookup.Tests.Providers;

public class IdealPostcodesProviderTests
{
    private const string Key = "ip-SENTINEL-KEY"; // gitleaks:allow

    private readonly FakeTimeProvider _time = new();
    private readonly FakeLogger<IdealPostcodesProvider> _log = new();

    private IdealPostcodesProvider Create(RecordingHandler handler, int timeoutSeconds = 5)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(IdealPostcodesProvider.ClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.ideal-postcodes.co.uk/") });
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.IdealPostcodes,
            TimeoutSeconds = timeoutSeconds,
            IdealPostcodes = { ApiKey = Key },
        };
        return new IdealPostcodesProvider(factory, new TestOptionsMonitor<AddressLookupOptions>(options), _time, _log);
    }

    private static AddressQuery Query(string text = "10 Downing") => new(text, null, ["GB"], 8);

    private static string Error(int code) => $"{{\"code\":{code},\"message\":\"msg with ip-SENTINEL-KEY\"}}";

    [Fact]
    public async Task Find_CallsAutocomplete()
    {
        var handler = RecordingHandler.Json(
            "{\"result\":{\"hits\":[{\"id\":\"paf_1\",\"suggestion\":\"10 Downing Street, London, SW1A 2AA\"},{\"id\":\"paf_2\",\"suggestion\":\"11 Downing Street, London, SW1A 2AB\"}]},\"code\":2000,\"message\":\"Success\"}");

        var results = await Create(handler).FindAsync(Query("10 Downing & Co"), CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/autocomplete/addresses", request.Uri.AbsolutePath);
        var query = System.Web.HttpUtility.ParseQueryString(request.Uri.Query);
        Assert.Equal("10 Downing & Co", query["query"]);
        Assert.Equal("8", query["limit"]);
        Assert.Null(query["api_key"]);
        Assert.Equal($"IDEALPOSTCODES api_key=\"{Key}\"", request.Headers["Authorization"]);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.IsContainer));
        Assert.Equal(("paf_1", "10 Downing Street", "London, SW1A 2AA"), (results[0].Id, results[0].Text, results[0].Description));
    }

    [Fact]
    public async Task Retrieve_CallsResolveGbr()
    {
        var handler = RecordingHandler.Json(
            "{\"result\":{\"organisation_name\":\"Cabinet Office\",\"line_1\":\"10 Downing Street\",\"line_2\":\"\",\"line_3\":\"\"," +
            "\"post_town\":\"London\",\"county\":\"\",\"postcode\":\"SW1A 2AA\"},\"code\":2000,\"message\":\"Success\"}");

        var address = await Create(handler).RetrieveAsync("paf_1", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/autocomplete/addresses/paf_1/gbr", request.Uri.AbsolutePath);
        Assert.DoesNotContain("api_key", request.Uri.Query);
        Assert.DoesNotContain("SENTINEL", request.Uri.OriginalString);
        Assert.Equal($"IDEALPOSTCODES api_key=\"{Key}\"", request.Headers["Authorization"]);
        Assert.NotNull(address);
        Assert.Equal(["Cabinet Office", "10 Downing Street"], address.Lines);
        Assert.Equal(("London", null, "SW1A 2AA", "GB", "United Kingdom"), (address.Town, address.County, address.Postcode, address.CountryIso2, address.Country));
        Assert.Equal(("idealPostcodes", "paf_1", "lookup"), (address.Provider, address.ProviderId, address.Source));
    }

    [Fact]
    public async Task Retrieve_EscapesTheId()
    {
        var handler = RecordingHandler.Json("{\"result\":{\"line_1\":\"x\",\"post_town\":\"t\",\"postcode\":\"SW1A 2AA\"},\"code\":2000}");

        await Create(handler).RetrieveAsync("a/b?c", CancellationToken.None);

        Assert.Equal("/v1/autocomplete/addresses/a%2Fb%3Fc/gbr", handler.Requests.Single().Uri.OriginalString.Split('?')[0].Replace("https://api.ideal-postcodes.co.uk",""));
    }

    [Theory]
    [InlineData(4010, HttpStatusCode.Unauthorized)]
    [InlineData(4011, HttpStatusCode.Unauthorized)]
    [InlineData(4020, HttpStatusCode.PaymentRequired)]
    [InlineData(4021, HttpStatusCode.PaymentRequired)]
    [InlineData(4291, HttpStatusCode.TooManyRequests)]
    [InlineData(5001, HttpStatusCode.InternalServerError)]
    public async Task Errors_MapToUnavailable(int code, HttpStatusCode status)
    {
        var provider = Create(RecordingHandler.Json(Error(code), status));

        var find = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query(), CancellationToken.None));
        var retrieve = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.RetrieveAsync("paf_1", CancellationToken.None));

        Assert.All(new[] { find, retrieve }, ex =>
        {
            Assert.Equal(("idealPostcodes", code.ToString()), (ex.Provider, ex.Code));
            Assert.Null(ex.InnerException);
            Assert.DoesNotContain("SENTINEL", ex.Message);
        });
    }

    [Fact]
    public async Task Errors_NotFound4048_GivesNullAndEmpty()
    {
        var provider = Create(RecordingHandler.Json(Error(4048), HttpStatusCode.NotFound));

        Assert.Null(await provider.RetrieveAsync("paf_1", CancellationToken.None));
        Assert.Empty(await provider.FindAsync(Query(), CancellationToken.None));
    }

    [Fact]
    public async Task ErrorWithoutJsonBody_MapsToHttpStatusCode()
    {
        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => Create(RecordingHandler.Json("<html>", HttpStatusCode.BadGateway)).FindAsync(Query(), CancellationToken.None));

        Assert.Equal("HTTP-502", ex.Code);
    }

    [Fact]
    public async Task Errors_LogThrottled_NeverCarriesKeyOrQuery()
    {
        var provider = Create(RecordingHandler.Json(Error(4020), HttpStatusCode.PaymentRequired));

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => provider.FindAsync(Query("SENTINEL QUERY"), CancellationToken.None));
        }

        var warning = Assert.Single(_log.Collector.GetSnapshot());
        Assert.Contains("idealPostcodes", warning.Message);
        Assert.Contains("4020", warning.Message);
        Assert.DoesNotContain("SENTINEL", warning.Message);
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
        var pending = Create(handler).FindAsync(Query(), CancellationToken.None);

        await started.Task;
        _time.Advance(TimeSpan.FromSeconds(6));

        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => pending);
        Assert.Equal("TIMEOUT", ex.Code);
    }

    [Fact]
    public async Task NoSourceIpHeader()
    {
        var handler = RecordingHandler.Json("{\"result\":{\"hits\":[]},\"code\":2000}");

        await Create(handler).FindAsync(Query(), CancellationToken.None);

        Assert.DoesNotContain(handler.Requests, r => r.Has("IDPC-Source-IP"));
    }

    [Fact]
    public async Task Retrieve_OrganisationAlreadyInLine1_NotRepeated()
    {
        var handler = RecordingHandler.Json(
            "{\"result\":{\"organisation_name\":\"Acme Ltd\",\"line_1\":\"Acme Ltd\",\"line_2\":\"1 High Street\",\"line_3\":\"\"," +
            "\"post_town\":\"Leeds\",\"county\":\"\",\"postcode\":\"LS1 4AP\"},\"code\":2000}");

        var address = await Create(handler).RetrieveAsync("paf_1", CancellationToken.None);

        Assert.Equal(["Acme Ltd", "1 High Street"], address!.Lines);
    }

    [Fact]
    public async Task BodyReadIoFailure_MapsToUnavailable()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FailingStream()),
        }));

        var ex = await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => Create(handler).FindAsync(Query(), CancellationToken.None));

        Assert.Equal("NETWORK", ex.Code);
        Assert.Null(ex.InnerException);
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset ip-SENTINEL-KEY");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException("connection reset ip-SENTINEL-KEY");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
