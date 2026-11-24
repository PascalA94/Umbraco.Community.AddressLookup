using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Controllers;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Providers.IdealPostcodes;
using Umbraco.Community.AddressLookup.Providers.Loqate;
using Umbraco.Forms.Core.Providers;

namespace Umbraco.Community.AddressLookup.Web;

/// <summary>The package's only composer. Register services, handlers and options here.</summary>
public sealed class AddressLookupComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        // ValidateOnStart so a missing key or a Stub provider in production stops the site at boot with the config key
        // named, instead of failing on the first visitor's search. No network is touched at start-up (pitfall P7).
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services
            .AddOptions<AddressLookupOptions>()
            .Bind(builder.Config.GetSection(AddressLookupOptions.SectionName))
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AddressLookupOptions>, AddressLookupOptionsValidator>());

        // Singletons: they hold no request state (pitfalls P1/P2). The limiter's counters are per instance by design.
        builder.Services.AddSingleton<LookupTicketService>();
        builder.Services.AddSingleton<AddressProofService>();
        builder.Services.AddSingleton<LookupRateLimiter>();

        // The proxy asks Forms, read-only, whether a ticket's field exists and what Countries it allows (pitfall P5).
        builder.Services.AddSingleton<IAddressFieldResolver, FormsAddressFieldResolver>();
        // Feeds the field view its ticket, settings and echoed values; a singleton because it holds no request state.
        builder.Services.AddSingleton<AddressFieldRenderer>();
        builder.WithCollectionBuilder<FieldCollectionBuilder>().Add<AddressLookupFieldType>();

        // One active provider, chosen from configuration at compose time. Unset means manual entry only: nothing is
        // registered and the proxy refuses lookups.
        switch (builder.Config.GetValue<ProviderKind?>($"{AddressLookupOptions.SectionName}:Provider"))
        {
            case ProviderKind.Loqate:
                builder.Services
                    .AddHttpClient(LoqateProvider.ClientName, c => c.BaseAddress = new Uri(LoqateProvider.BaseAddress))
                    .ConfigurePrimaryHttpMessageHandler(ProviderHttp.CreateHandler)
                    // The URI is constant and the key and text travel in the body, but the factory's handlers also log
                    // request details at lower levels. Removed so nothing about a call is logged by the framework.
                    .RemoveAllLoggers();
                builder.Services.AddSingleton<IAddressLookupProvider, LoqateProvider>();
                break;
            case ProviderKind.IdealPostcodes:
                builder.Services
                    .AddHttpClient(IdealPostcodesProvider.ClientName, c => c.BaseAddress = new Uri(IdealPostcodesProvider.BaseAddress))
                    .ConfigurePrimaryHttpMessageHandler(ProviderHttp.CreateHandler)
                    // The Retrieve URI path holds the provider id and the Find URI holds the visitor's text. The factory's
                    // handlers log them at Information. A host that swaps in Serilog's logger factory (as UseSerilog does)
                    // ignores the AddFilter rule below, so the handlers are removed instead of filtered. Unverified against
                    // Umbraco's own logger setup: LeakTests reproduces it with SerilogLoggerFactory only.
                    .RemoveAllLoggers();
                builder.Services.AddSingleton<IAddressLookupProvider, IdealPostcodesProvider>();
                // Belt and braces for hosts whose logging does honour filter rules (design 5.1). LeakTests proves the
                // Serilog-backed case.
                builder.Services.AddLogging(l => l.AddFilter($"System.Net.Http.HttpClient.{IdealPostcodesProvider.ClientName}", LogLevel.None));
                break;
            case ProviderKind.Stub:
                builder.Services.AddSingleton<IAddressLookupProvider, StubProvider>();
                break;
        }
    }
}
