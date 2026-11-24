using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Forms.Web.Models;

namespace Umbraco.Community.AddressLookup.IntegrationTests;

/// <summary>
/// The field view, rendered through the Razor class library in the real host, so the markup the script and the posted form
/// depend on is covered at PR time (not only by the e2e run before a release), and so no key can reach a page unnoticed.
/// </summary>
public abstract class FieldViewRenderingTestBase : ApiTestBase
{
    // Fake keys that only mean something to this test. Never real credentials.
    protected const string ServerKey = "lq-VIEWTEST-SERVER-KEY"; // gitleaks:allow
    protected const string BrowserKey = "AA11-BB22-CC33-DD44"; // gitleaks:allow

    private const string ViewPath = "~/Views/Partials/Forms/Themes/default/Fieldtypes/FieldType.AddressLookup.cshtml";

    protected abstract IReadOnlyDictionary<string, string> AddressLookupSettings { get; }

    public override void Setup()
    {
        // Read when the host is built, which Setup does, so it has to be in place first. Provider is read at compose time.
        foreach (var (key, value) in AddressLookupSettings)
        {
            InMemoryConfiguration[$"AddressLookup:{key}"] = value;
        }

        base.Setup();
    }

    protected async Task<string> RenderAsync(Guid? fieldId = null)
    {
        using var scope = Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("localhost");

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var viewResult = scope.ServiceProvider.GetRequiredService<IRazorViewEngine>().GetView(null, ViewPath, isMainPage: false);
        Assert.That(viewResult.Success, Is.True, $"View not found. Searched: {string.Join(", ", viewResult.SearchedLocations ?? [])}");

        var id = (fieldId ?? Guid.NewGuid()).ToString();
        var model = new FieldViewModel
        {
            Id = "field_1", Name = id, Caption = "Home address", FormId = Guid.NewGuid(), Mandatory = true,
            AdditionalSettings = new Dictionary<string, string> { ["Countries"] = "GB", ["AllowManualEntry"] = "true" },
        };
        var viewData = new ViewDataDictionary<FieldViewModel>(new EmptyModelMetadataProvider(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(http, scope.ServiceProvider.GetRequiredService<ITempDataProvider>());

        await using var writer = new StringWriter();
        var viewContext = new ViewContext(actionContext, viewResult.View!, viewData, tempData, writer, new HtmlHelperOptions());
        await viewResult.View!.RenderAsync(viewContext);
        return writer.ToString();
    }

    protected static string Attribute(string html, string name)
    {
        var match = Regex.Match(html, $@"\b{Regex.Escape(name)}=""([^""]*)""");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : string.Empty;
    }
}

public class ProxyModeFieldViewTests : FieldViewRenderingTestBase
{
    protected override IReadOnlyDictionary<string, string> AddressLookupSettings => new Dictionary<string, string>
    {
        ["Provider"] = "Loqate",
        ["Mode"] = "Proxy",
        ["Loqate:ApiKey"] = ServerKey,
        ["Loqate:CaptureKey"] = BrowserKey,
    };

    [Test]
    public async Task Proxy_Renders_TheDataAttributesTheScriptReads_AndTheInputsTheFormPosts()
    {
        var fieldId = Guid.NewGuid();

        var html = await RenderAsync(fieldId);

        Assert.That(html, Does.Contain("data-address-lookup"));
        Assert.That(Attribute(html, "data-mode"), Is.EqualTo("proxy"));
        Assert.That(Attribute(html, "data-endpoint"), Is.EqualTo("/umbraco/surface/AddressLookup"));
        Assert.That(Attribute(html, "data-ticket"), Is.Not.Empty, "a ticket is how the proxy knows the field");
        Assert.That(Attribute(html, "data-token"), Is.Not.Empty, "the antiforgery token the proxy requires");
        Assert.That(Attribute(html, "data-countries"), Is.EqualTo("GB"));
        Assert.That(Attribute(html, "data-id"), Is.EqualTo("field_1"));
        foreach (var part in new[] { "line1", "line2", "line3", "town", "county", "postcode", "country", "providerId", "proof" })
        {
            Assert.That(html, Does.Contain($"name=\"{fieldId}.{part}\""), part);
        }

        Assert.That(html, Does.Contain("data-al-search"));
        Assert.That(html, Does.Contain("data-al-manual-button"));
    }

    [Test]
    public async Task Proxy_NoServerKeyAndNoBrowserKey_InThePage()
    {
        var html = await RenderAsync();

        Assert.That(html, Does.Not.Contain(ServerKey));
        Assert.That(html, Does.Not.Contain(BrowserKey), "the browser key is for Capture mode only");
        Assert.That(Attribute(html, "data-capture-key"), Is.Empty);
    }
}

public class CaptureModeFieldViewTests : FieldViewRenderingTestBase
{
    protected override IReadOnlyDictionary<string, string> AddressLookupSettings => new Dictionary<string, string>
    {
        ["Provider"] = "Loqate",
        ["Mode"] = "Capture",
        ["Loqate:ApiKey"] = ServerKey,
        ["Loqate:CaptureKey"] = BrowserKey,
    };

    [Test]
    public async Task Capture_PrintsTheBrowserKey_NeverTheServerKey_AndIssuesNoTicket()
    {
        var html = await RenderAsync();

        Assert.That(Attribute(html, "data-mode"), Is.EqualTo("capture"));
        Assert.That(Attribute(html, "data-capture-key"), Is.EqualTo(BrowserKey));
        Assert.That(html, Does.Not.Contain(ServerKey));
        Assert.That(Attribute(html, "data-ticket"), Is.Empty);
        Assert.That(Attribute(html, "data-token"), Is.Empty);
    }
}
