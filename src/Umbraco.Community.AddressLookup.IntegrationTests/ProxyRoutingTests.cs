using System.Net;

namespace Umbraco.Community.AddressLookup.IntegrationTests;

/// <summary>
/// The proxy is reachable on the surface URL and refuses what it must, on the real pipeline of each line. The
/// antiforgery filter runs before the controller is built, so these hold even before a field type is registered.
/// </summary>
public class ProxyRoutingTests : ApiTestBase
{
    [TestCase("Find")]
    [TestCase("Retrieve")]
    public async Task Post_WithoutAntiforgeryToken_Returns400(string action)
    {
        using var client = CreateAnonymousClient();
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = "x", ["text"] = "ST1", ["id"] = "x" });

        var response = await client.PostAsync($"/umbraco/surface/AddressLookup/{action}", body);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        // The controller's own refusals carry {"error":"invalid"}; the framework's antiforgery failure has no body. An
        // empty body proves the antiforgery filter answered, not the controller, so removing [ValidateAntiForgeryToken]
        // (pitfall S5) fails this test even after a field resolver is registered.
        Assert.That(await response.Content.ReadAsStringAsync(), Is.Empty);
    }

    [TestCase("Find")]
    [TestCase("Retrieve")]
    public async Task Get_IsNotAllowed(string action)
    {
        using var client = CreateAnonymousClient();

        var response = await client.GetAsync($"/umbraco/surface/AddressLookup/{action}");

        // 405 from MVC, or 404 when Umbraco's content routing answers first: either way a GET is never served.
        Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound));
    }
}
