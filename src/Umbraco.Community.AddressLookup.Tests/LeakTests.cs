using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Controllers;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Providers.IdealPostcodes;
using Umbraco.Community.AddressLookup.Providers.Loqate;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Tests.Support;
using Umbraco.Community.AddressLookup.Web;
using Umbraco.Forms.Core.Models;

namespace Umbraco.Community.AddressLookup.Tests;

/// <summary>
/// An address, a search and a key are personal data or secrets (design 5.1, 5.4). Every failure path must keep them out
/// of logs, exception text, <c>ToString()</c> and responses. The sentinels below are planted in the request, in the
/// provider's answer and in the configuration, so any path that echoes one of them fails here.
/// </summary>
public class LeakTests
{
    private const string LoqateKey = "lq-SENTINEL-KEY"; // gitleaks:allow
    private const string IdealKey = "ip-SENTINEL-KEY"; // gitleaks:allow
    private const string Query = "SENTINEL QUERY";
    private const string Line = "1 Sentinel Street";

    private static readonly Guid FormId = Guid.NewGuid();
    private static readonly Guid FieldId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
    private readonly IDataProtectionProvider _protection = DataProtectionProvider.Create("address-lookup-leak-tests");

    // One collector for every component under test, so a leak anywhere in a scenario is seen.
    private readonly FakeLoggerProvider _logs = new();

    private const string ClientIp = "203.0.113.77";

    private static readonly string[] Sentinels = ["SENTINEL", "Sentinel Street", LoqateKey, IdealKey, ClientIp];

    // Per-test values that are only known at run time, such as the lookup tickets.
    private readonly List<string> _extraSentinels = [];

    private void AssertClean(string what, string? text)
    {
        foreach (var sentinel in Sentinels.Concat(_extraSentinels))
        {
            Assert.False(
                text?.Contains(sentinel, StringComparison.OrdinalIgnoreCase) ?? false,
                $"{what} leaked '{sentinel}'");
        }
    }

    private void AssertLogsClean()
    {
        foreach (var record in _logs.Collector.GetSnapshot())
        {
            AssertClean("log message", record.Message);
            AssertClean("log exception", record.Exception?.ToString());
            AssertClean("log state", string.Join(";", record.StructuredState?.Select(p => $"{p.Key}={p.Value}") ?? []));
            AssertClean("log scopes", string.Join(";", record.Scopes.Select(s => s?.ToString())));
        }
    }

    private ILogger<T> Logger<T>() => new LoggerFactory([_logs]).CreateLogger<T>();

    // ---------------------------------------------------------------- providers

    public static TheoryData<string, string> ProviderFailures()
    {
        var data = new TheoryData<string, string>();
        foreach (var provider in new[] { "loqate", "ideal" })
        {
            foreach (var scenario in new[]
            {
                "http500", "http401", "http429", "errorBody", "malformed", "timeout", "networkIo", "httpRequestException", "oversize",
                "redirect",
            })
            {
                data.Add(provider, scenario);
            }
        }

        return data;
    }

    private static string ErrorBody(string provider) => provider == "loqate"
        ? $"{{\"Items\":[{{\"Error\":\"2\",\"Description\":\"Unknown key {LoqateKey} for {Query} at {Line}\",\"Cause\":\"{IdealKey}\",\"Resolution\":\"{Line}\"}}]}}"
        : $"{{\"code\":4010,\"message\":\"Invalid key {IdealKey} {Query} {Line}\"}}";

    private static RecordingHandler FailingHandler(string provider, string scenario)
    {
        var echo = $"{LoqateKey} {IdealKey} {Query} {Line}";
        return scenario switch
        {
            "http500" => RecordingHandler.Json(ErrorBody(provider), HttpStatusCode.InternalServerError),
            "http401" => RecordingHandler.Json(ErrorBody(provider), HttpStatusCode.Unauthorized),
            "http429" => RecordingHandler.Json(ErrorBody(provider), HttpStatusCode.TooManyRequests),
            "errorBody" => RecordingHandler.Json(ErrorBody(provider)),
            "malformed" => RecordingHandler.Json($"<html>{echo}</html>"),
            "oversize" => RecordingHandler.Json($"{{\"Items\":[],\"result\":{{}},\"Pad\":\"{echo}{new string('a', 70 * 1024)}\"}}"),
            "redirect" => new RecordingHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri($"https://evil.example/?k={Uri.EscapeDataString(echo)}");
                return Task.FromResult(response);
            }),
            "networkIo" => new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FailingStream($"connection reset {echo}")),
            })),
            "httpRequestException" => new RecordingHandler((_, _) =>
                throw new HttpRequestException($"no route to {echo}", new IOException(echo))),
            "timeout" => new RecordingHandler(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage();
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    private IAddressLookupProvider RealProvider(string provider, RecordingHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var options = new AddressLookupOptions
        {
            TimeoutSeconds = 5,
            Loqate = { ApiKey = LoqateKey },
            IdealPostcodes = { ApiKey = IdealKey },
        };
        var monitor = new TestOptionsMonitor<AddressLookupOptions>(options);
        if (provider == "loqate")
        {
            factory.CreateClient(LoqateProvider.ClientName)
                .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(LoqateProvider.BaseAddress) });
            return new LoqateProvider(factory, monitor, _time, Logger<LoqateProvider>());
        }

        factory.CreateClient(IdealPostcodesProvider.ClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri(IdealPostcodesProvider.BaseAddress) });
        return new IdealPostcodesProvider(factory, monitor, _time, Logger<IdealPostcodesProvider>());
    }

    /// <summary>Runs a call that must fail, advancing the clock past the timeout when the scenario is a hang.</summary>
    private async Task<Exception> FailureOfAsync(string scenario, RecordingHandler handler, Func<Task> call)
    {
        var pending = call();
        if (scenario == "timeout")
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (handler.Requests.Count == 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new Xunit.Sdk.XunitException("The provider call never reached the handler within 10 seconds.");
                }

                await Task.Delay(5);
            }

            _time.Advance(TimeSpan.FromSeconds(6));
        }

        return await Record.ExceptionAsync(() => pending) ?? throw new Xunit.Sdk.XunitException($"{scenario} did not fail");
    }

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task NoSecretsOrPersonalData_ProviderFailure(string provider, string scenario)
    {
        var query = new AddressQuery(Query, null, ["GB"], 8);

        foreach (var retrieve in new[] { false, true })
        {
            var handler = FailingHandler(provider, scenario);
            var lookup = RealProvider(provider, handler);

            var ex = await FailureOfAsync(scenario, handler, retrieve
                ? () => lookup.RetrieveAsync("SENTINEL-ID", CancellationToken.None)
                : () => lookup.FindAsync(query, CancellationToken.None));

            Assert.IsType<AddressLookupUnavailableException>(ex);
            Assert.Null(ex.InnerException);
            AssertClean("exception message", ex.Message);
            AssertClean("exception text", ex.ToString());
        }

        // Positive control: a clean log is only meaningful if the failure was logged at all.
        Assert.True(_logs.Collector.Count > 0, "the failure should have been logged");
        AssertLogsClean();
    }

    // ---------------------------------------------------------------- proxy

    public static TheoryData<string> ProxyCases() =>
        new()
        {
            "badTicket", "badText", "controlCharsInText", "unknownField", "noProvider", "providerHttp500", "providerNetwork",
            "providerRequestException", "throttled", "dailyCap", "countryNotAllowed", "notFound", "badId",
        };

    [Theory]
    [MemberData(nameof(ProxyCases))]
    public async Task NoSecretsOrPersonalData_ProxyFailure(string scenario)
    {
        var options = new AddressLookupOptions { Provider = ProviderKind.Loqate, Loqate = { ApiKey = LoqateKey } };
        var fields = Substitute.For<IAddressFieldResolver>();
        fields.Resolve(FormId, FieldId).Returns(new AddressFieldInfo(["GB"]));
        var tickets = new LookupTicketService(_protection, _time);
        var ticket = tickets.Issue(FormId, FieldId);
        var otherFieldTicket = tickets.Issue(FormId, Guid.NewGuid());
        _extraSentinels.AddRange([ticket, otherFieldTicket]);

        var provider = Substitute.For<IAddressLookupProvider>();
        provider.FindAsync(Arg.Any<AddressQuery>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<AddressSuggestion>());
        provider.RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AddressValue([Line], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "stub", "id", "lookup"));

        IAddressLookupProvider[] providers = [provider];
        var call = scenario switch
        {
            "providerHttp500" => Real("http500"),
            "providerNetwork" => Real("networkIo"),
            "providerRequestException" => Real("httpRequestException"),
            _ => null,
        };
        if (call is not null)
        {
            providers = [call];
        }

        switch (scenario)
        {
            case "noProvider":
                providers = [];
                break;
            case "throttled":
                options.RateLimits.FindPerMinute = 1;
                break;
            case "dailyCap":
                options.RateLimits.RetrievePerDay = 1;
                break;
            case "countryNotAllowed":
                provider.RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(new AddressValue([Line], "Paris", null, "75001", "FR", "France", "stub", "id", "lookup"));
                break;
            case "notFound":
                provider.RetrieveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AddressValue?)null);
                break;
        }

        IAddressLookupProvider Real(string failure) => RealProvider("loqate", FailingHandler("loqate", failure));

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
        var monitor = new TestOptionsMonitor<AddressLookupOptions>(options);
        using var limiter = new LookupRateLimiter(monitor, _time);
        var controller = new AddressLookupController(
            monitor, tickets, new AddressProofService(_protection, _time), limiter, fields, providers, Logger<AddressLookupController>())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var responses = new List<IActionResult>();
        switch (scenario)
        {
            case "badTicket":
                responses.Add(await controller.Find($"x{Query}", Query, null, default));
                responses.Add(await controller.Retrieve(Query, Line, default));
                break;
            case "badText":
                responses.Add(await controller.Find(ticket, "S", null, default));
                break;
            case "controlCharsInText":
                responses.Add(await controller.Find(ticket, $"{Query}\u0000{Line}", null, default));
                responses.Add(await controller.Find(ticket, Query, $"{Line}\u0000", default));
                break;
            case "unknownField":
                responses.Add(await controller.Find(otherFieldTicket, Query, null, default));
                responses.Add(await controller.Retrieve(otherFieldTicket, "SENTINEL-ID", default));
                break;
            case "throttled":
                responses.Add(await controller.Find(ticket, Query, null, default));
                responses.Add(await controller.Find(ticket, Query, null, default));
                break;
            case "dailyCap":
                responses.Add(await controller.Retrieve(ticket, "SENTINEL-ID", default));
                responses.Add(await controller.Retrieve(ticket, "SENTINEL-ID", default));
                break;
            case "countryNotAllowed" or "notFound":
                responses.Add(await controller.Retrieve(ticket, "SENTINEL-ID", default));
                break;
            case "badId":
                responses.Add(await controller.Retrieve(ticket, $"SENTINEL\u0000{Line}", default));
                break;
            default:
                responses.Add(await controller.Find(ticket, Query, null, default));
                responses.Add(await controller.Retrieve(ticket, "SENTINEL-ID", default));
                break;
        }

        // A successful Retrieve returns the address by design, so only the refusals are scanned. The first dailyCap call
        // (and the first throttled Find) is the allowed one.
        var refused = scenario is "dailyCap" or "throttled" ? responses.Skip(1) : responses;
        foreach (var response in refused)
        {
            var json = Assert.IsType<JsonResult>(response);
            Assert.True((json.StatusCode ?? 200) is 400 or 429 or 503, $"{scenario} gave {json.StatusCode}");
            AssertClean("response body", JsonSerializer.Serialize(json.Value));
        }

        AssertClean("response headers", string.Join(";", controller.Response.Headers.Select(h => $"{h.Key}={h.Value}")));
        if (scenario is "providerHttp500" or "providerNetwork" or "providerRequestException" or "dailyCap")
        {
            Assert.True(_logs.Collector.Count > 0, "the failure should have been logged");
        }

        AssertLogsClean();
    }

    // ---------------------------------------------------------------- submit

    public static TheoryData<string> SubmitCases() =>
        new() { "angleBrackets", "tooLong", "badPostcode", "countryNotAllowed", "controlChar", "formula", "forgedProof", "invisibleLetter", "halfAddress" };

    [Theory]
    [MemberData(nameof(SubmitCases))]
    public void NoSecretsOrPersonalData_SubmitValidation(string scenario)
    {
        var field = new Field { Id = FieldId, Mandatory = true, Settings = new Dictionary<string, string>() };
        var parts = new Dictionary<string, string>
        {
            ["line1"] = Line,
            ["town"] = "Leeds",
            ["postcode"] = "LS1 4AP",
            ["country"] = "GB",
        };

        switch (scenario)
        {
            case "angleBrackets": parts["line2"] = $"<b>{Line}</b>"; break;
            case "tooLong": parts["line1"] = $"{Line} {new string('x', 300)}"; break;
            case "badPostcode": parts["postcode"] = "SENTINEL"; break;
            case "countryNotAllowed": parts["country"] = "SENTINEL"; break;
            case "controlChar": parts["line1"] = $"1 Sentinel\u0001 Street"; break;
            case "formula": parts["line1"] = $"={Line}"; break;
            case "invisibleLetter": parts["line1"] = $"{Line}ㅤ"; break;
            case "halfAddress": parts.Remove("postcode"); parts.Remove("town"); break;
            case "forgedProof":
                field.Settings["RequireLookup"] = "true";
                parts["providerId"] = "SENTINEL-ID";
                parts["proof"] = "SENTINEL-PROOF";
                break;
        }

        var provider = Substitute.For<IAddressLookupProvider>();
        provider.Alias.Returns("stub");
        var type = new AddressLookupFieldType(
            new TestOptionsMonitor<AddressLookupOptions>(new AddressLookupOptions { Provider = ProviderKind.Stub }),
            new AddressProofService(_protection, _time),
            [provider]);
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(parts.ToDictionary(p => $"{field.Id}.{p.Key}", p => new StringValues(p.Value)));

        var errors = type.ValidateField(new Form(), field, [], context, null!, null!).ToList();
        Assert.NotEmpty(errors);
        foreach (var error in errors)
        {
            AssertClean("validation error", error);
        }

        // Storing never throws on a refused address, and a forged proof leaves no provider id or proof in the stored value.
        var stored = Record.Exception(() => type.ProcessSubmittedValue(field, [], context).Cast<object>().Select(v => v.ToString()).ToList());
        Assert.Null(stored);
        if (scenario == "forgedProof")
        {
            foreach (var value in type.ProcessSubmittedValue(field, [], context).Cast<object>())
            {
                Assert.DoesNotContain("SENTINEL-PROOF", value.ToString());
                Assert.DoesNotContain("SENTINEL-ID", value.ToString());
            }
        }
    }

    // ---------------------------------------------------------------- ToString, options

    [Fact]
    public void NoSecretsOrPersonalData_ToString()
    {
        var address = new AddressValue([Line, "SENTINEL"], "Sentinel town", "SENTINEL", "SENTINEL", "GB", "SENTINEL", "loqate", "SENTINEL-ID", "lookup");
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate,
            Loqate = { ApiKey = LoqateKey, CaptureKey = "lq-SENTINEL-CAPTURE" }, // gitleaks:allow
            IdealPostcodes = { ApiKey = IdealKey },
        };
        var render = new AddressFieldRender(
            "name", "id", "Home address", true, string.Empty, true, false, ["GB"], true, true, "SENTINEL-TICKET", "lq-SENTINEL-CAPTURE", "4.05",
            true, new PostedAddress(Line, Line, Line, Line, Line, Line, Line, "SENTINEL-ID", "SENTINEL-PROOF"), []);
        var texts = new object?[]
        {
            address,
            new AddressQuery(Query, "SENTINEL-CONTAINER", ["GB"], 8),
            new AddressSuggestion("SENTINEL-ID", Line, Query, false),
            new PostedAddress(Line, Line, Line, Line, Line, Line, Line, "SENTINEL-ID", "SENTINEL-PROOF"),
            render,
            options,
            options.Loqate,
            options.IdealPostcodes,
            new AddressLookupUnavailableException("loqate", "2"),
            new LookupTicket(FormId, FieldId, _time.GetUtcNow()),
            new AddressValue.Wire(1, [Line], "Sentinel town", "SENTINEL", "SENTINEL", "GB", "SENTINEL", "loqate", "SENTINEL-ID", "lookup"),
            new RateLimitDecision(false, TimeSpan.FromSeconds(1), true, true),
            JsonSerializer.Serialize(options),
        };

        foreach (var text in texts)
        {
            AssertClean(text?.GetType().Name ?? "null", text?.ToString());
        }
    }

    [Theory]
    [InlineData("captureEqualsServer")]
    [InlineData("stubInProduction")]
    [InlineData("missingOtherKey")]
    [InlineData("captureWithIdeal")]
    public void NoSecretsOrPersonalData_OptionsValidationMessages(string scenario)
    {
        var options = scenario switch
        {
            "captureEqualsServer" => new AddressLookupOptions
            {
                Provider = ProviderKind.Loqate, Mode = LookupMode.Capture, Loqate = { ApiKey = LoqateKey, CaptureKey = LoqateKey },
            },
            "stubInProduction" => new AddressLookupOptions
            {
                Provider = ProviderKind.Stub, Loqate = { ApiKey = LoqateKey }, IdealPostcodes = { ApiKey = IdealKey },
            },
            "missingOtherKey" => new AddressLookupOptions { Provider = ProviderKind.Loqate, IdealPostcodes = { ApiKey = IdealKey } },
            _ => new AddressLookupOptions
            {
                Provider = ProviderKind.IdealPostcodes, Mode = LookupMode.Capture, IdealPostcodes = { ApiKey = IdealKey }, Loqate = { ApiKey = LoqateKey },
            },
        };
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns("Production");

        var result = new AddressLookupOptionsValidator(host).Validate(null, options);

        Assert.True(result.Failed);
        AssertClean("validation failure", string.Join(" | ", result.Failures!));
    }

    [Fact]
    public void NoSecretsOrPersonalData_CaptureRenderRefusesServerKeyWithWhitespace()
    {
        var options = new AddressLookupOptions
        {
            Provider = ProviderKind.Loqate, Mode = LookupMode.Capture, Loqate = { ApiKey = LoqateKey, CaptureKey = $" {LoqateKey} " },
        };
        var renderer = new AddressFieldRenderer(
            new TestOptionsMonitor<AddressLookupOptions>(options),
            new LookupTicketService(_protection, _time),
            new AddressProofService(_protection, _time),
            [Substitute.For<IAddressLookupProvider>()]);

        var render = renderer.Create(new AddressFieldRenderRequest(FormId, FieldId.ToString(), "f", "Address", false, new Dictionary<string, string>(), null, true, null));

        // The model the view prints holds no key at all when the "browser" key is really the server key.
        Assert.False(render.CaptureEnabled);
        Assert.Null(render.CaptureKey);
        AssertClean("render model", string.Join("|", render.ToString(), render.CaptureKey, render.Ticket, string.Join(",", render.ConfigurationErrors)));
    }

    // ---------------------------------------------------------------- Serilog-backed host

    private sealed class CollectingSink : ILogEventSink
    {
        public List<string> Lines { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Lines)
            {
                Lines.Add($"{logEvent.Level} {SourceContext(logEvent)} {logEvent.RenderMessage()} {logEvent.Exception} " +
                          string.Join(";", logEvent.Properties.Select(p => $"{p.Key}={p.Value}")));
            }
        }
    }

    private static string? SourceContext(Serilog.Events.LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value) ? value.ToString() : null;

    /// <summary>
    /// Composes the real composer, then swaps in Serilog's own logger factory exactly as <c>UseSerilog</c> does under
    /// Umbraco. That factory ignores <c>AddFilter</c> rules, which is why the README tells sites to override the
    /// <c>System.Net.Http.HttpClient</c> level in Serilog and why the package removes the HttpClient logging handlers itself.
    /// </summary>
    private static ServiceProvider ComposeUnderSerilog(string provider, Serilog.ILogger serilog, HttpMessageHandler handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AddressLookup:Provider"] = provider == "loqate" ? "Loqate" : "IdealPostcodes",
            ["AddressLookup:IdealPostcodes:ApiKey"] = IdealKey,
            ["AddressLookup:Loqate:ApiKey"] = LoqateKey,
        }).Build();
        var services = new ServiceCollection();
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns("Production");
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(host);
        services.AddSingleton<IConfiguration>(config);
        new AddressLookupComposer().Compose(
            new UmbracoBuilder(services, config, new TypeLoader(Substitute.For<ITypeFinder>(), NullLogger<TypeLoader>.Instance, [])));

        services.AddHttpClient(provider == "loqate" ? LoqateProvider.ClientName : IdealPostcodesProvider.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(new SerilogLoggerFactory(serilog)));
        return services.BuildServiceProvider();
    }

    private static Serilog.ILogger BuildSerilog(CollectingSink sink, bool withReadmeOverride)
    {
        var configuration = new Serilog.LoggerConfiguration().MinimumLevel.Verbose();
        if (withReadmeOverride)
        {
            configuration.MinimumLevel.Override("System.Net.Http.HttpClient.community.addressLookup", LogEventLevel.Warning);
        }

        return configuration.WriteTo.Sink(sink).CreateLogger();
    }

    [Theory]
    [InlineData("ideal", false)]
    [InlineData("ideal", true)]
    [InlineData("loqate", false)]
    [InlineData("loqate", true)]
    public async Task NoSecretsOrPersonalData_SerilogBackedHost(string providerName, bool withReadmeOverride)
    {
        var sink = new CollectingSink();
        var handler = new RecordingHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent(ErrorBody(providerName)),
        }));
        await using var provider = ComposeUnderSerilog(providerName, BuildSerilog(sink, withReadmeOverride), handler);
        var lookup = provider.GetRequiredService<IAddressLookupProvider>();

        await Assert.ThrowsAsync<AddressLookupUnavailableException>(
            () => lookup.FindAsync(new AddressQuery(Query, null, ["GB"], 8), CancellationToken.None));
        await Assert.ThrowsAsync<AddressLookupUnavailableException>(() => lookup.RetrieveAsync("SENTINEL-ID", CancellationToken.None));

        // The calls really reached the (stubbed) network carrying the data, so a clean log is not an artefact: Ideal Postcodes
        // puts the id in the path and the text in the query, Loqate puts both in the POST body.
        Assert.Contains(handler.Requests, r =>
            r.Uri.AbsolutePath.Contains("SENTINEL-ID", StringComparison.Ordinal) || r.Body.Contains("SENTINEL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sink.Lines, l => l.Contains(providerName == "loqate" ? "loqate" : "idealPostcodes"));
        foreach (var line in sink.Lines)
        {
            AssertClean("serilog event", line);
            Assert.DoesNotContain("System.Net.Http.HttpClient.community.addressLookup", line);
        }
    }

    [Fact]
    public async Task NoSecretsOrPersonalData_SerilogHarnessCanSeeAHttpClientLeak()
    {
        // Control: a client the package does not own, logged by the same factory, puts the request path in the log (the query string is redacted by .NET, a path segment is not, and Ideal Postcodes carries the provider id in its path). This
        // proves the Serilog test above would fail if the package's client were still logged.
        var sink = new CollectingSink();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("control").ConfigurePrimaryHttpMessageHandler(() => RecordingHandler.Json("{}"));
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(new SerilogLoggerFactory(BuildSerilog(sink, false))));
        await using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("control");
        await client.GetAsync($"https://example.test/search/{Uri.EscapeDataString(Query)}");

        Assert.Contains(sink.Lines, l => l.Contains("SENTINEL", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FailingStream(string message) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(message);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new IOException(message);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
