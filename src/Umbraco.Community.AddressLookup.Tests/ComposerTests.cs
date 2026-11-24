using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Providers.IdealPostcodes;
using Umbraco.Community.AddressLookup.Providers.Loqate;
using Umbraco.Community.AddressLookup.Web;

namespace Umbraco.Community.AddressLookup.Tests;

public class ComposerTests
{
    private static ServiceProvider Compose(Dictionary<string, string?> settings, string environment = "Development")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton(host);
        services.AddSingleton<IConfiguration>(config);

        var builder = new UmbracoBuilder(services, config, new TypeLoader(Substitute.For<ITypeFinder>(), NullLogger<TypeLoader>.Instance, []));
        new AddressLookupComposer().Compose(builder);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void Options_BindCaseInsensitively_AndUseDefaults()
    {
        using var provider = Compose(new()
        {
            ["AddressLookup:Provider"] = "idealpostcodes",
            ["AddressLookup:IdealPostcodes:ApiKey"] = "k",
            ["AddressLookup:MaxSuggestions"] = "5",
        });

        var options = provider.GetRequiredService<IOptions<AddressLookupOptions>>().Value;

        Assert.Equal(ProviderKind.IdealPostcodes, options.Provider);
        Assert.Equal(5, options.MaxSuggestions);
        Assert.Equal(LookupMode.Proxy, options.Mode);
        Assert.Equal(30, options.RateLimits.FindPerMinute);
    }

    [Fact]
    public void InvalidOptions_FailOnAccess_NamingKey()
    {
        using var provider = Compose(new() { ["AddressLookup:Provider"] = "Loqate" });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AddressLookupOptions>>().Value);

        Assert.Contains("AddressLookup:Loqate:ApiKey", ex.Message);
    }

    [Fact]
    public void ValidateOnStart_IsRegistered()
    {
        using var provider = Compose(new() { ["AddressLookup:Provider"] = "Loqate" });

        var startup = provider.GetServices<IStartupValidator>().ToList();

        Assert.NotEmpty(startup);
        Assert.Throws<OptionsValidationException>(() => startup[0].Validate());
    }

    [Fact]
    public void Stub_RegistersStubProvider()
    {
        using var provider = Compose(new() { ["AddressLookup:Provider"] = "Stub" });

        var registered = provider.GetRequiredService<IAddressLookupProvider>();

        Assert.IsType<StubProvider>(registered);
        Assert.Same(registered, provider.GetRequiredService<IAddressLookupProvider>());
    }

    [Fact]
    public void NoProvider_RegistersNoProvider()
    {
        using var provider = Compose(new());

        Assert.Null(provider.GetService<IAddressLookupProvider>());
    }

    [Fact]
    public void Loqate_RegistersProviderAndNamedClient()
    {
        using var provider = Compose(new() { ["AddressLookup:Provider"] = "Loqate", ["AddressLookup:Loqate:ApiKey"] = "k" });

        Assert.IsType<LoqateProvider>(provider.GetRequiredService<IAddressLookupProvider>());
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(LoqateProvider.ClientName);
        Assert.Equal(new Uri(LoqateProvider.BaseAddress), client.BaseAddress);
    }

    [Fact]
    public void IdealPostcodes_RegistersProviderAndSilencesHttpClientLogs()
    {
        using var provider = Compose(new() { ["AddressLookup:Provider"] = "IdealPostcodes", ["AddressLookup:IdealPostcodes:ApiKey"] = "k" });

        Assert.IsType<IdealPostcodesProvider>(provider.GetRequiredService<IAddressLookupProvider>());
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(IdealPostcodesProvider.ClientName);
        Assert.Equal(new Uri(IdealPostcodesProvider.BaseAddress), client.BaseAddress);

        // The GET URI carries the key, and the HttpClient handlers log it at Information.
        var loggers = provider.GetRequiredService<ILoggerFactory>();
        foreach (var suffix in new[] { "LogicalHandler", "ClientHandler" })
        {
            var logger = loggers.CreateLogger($"System.Net.Http.HttpClient.{IdealPostcodesProvider.ClientName}.{suffix}");
            Assert.False(logger.IsEnabled(LogLevel.Critical));
        }
    }

    [Fact]
    public void SecurityServices_AreSingletons()
    {
        using var provider = Compose(new());

        Assert.Same(provider.GetRequiredService<LookupTicketService>(), provider.GetRequiredService<LookupTicketService>());
        Assert.Same(provider.GetRequiredService<AddressProofService>(), provider.GetRequiredService<AddressProofService>());
        Assert.Same(provider.GetRequiredService<LookupRateLimiter>(), provider.GetRequiredService<LookupRateLimiter>());
    }
}
