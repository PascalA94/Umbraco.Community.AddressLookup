using Microsoft.Extensions.DependencyInjection;
using Umbraco.Community.AddressLookup.Controllers;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Forms.Core.Providers;

namespace Umbraco.Community.AddressLookup.IntegrationTests;

/// <summary>The package's composer wires the field type and the proxy's field resolver into a real Forms host.</summary>
public class FieldTypeRegistrationTests : ApiTestBase
{
    [Test]
    public void FieldType_IsInTheFormsFieldCollection()
    {
        var fieldTypes = Services.GetRequiredService<FieldCollection>();

        var type = fieldTypes.SingleOrDefault(f => f.Id == AddressLookupFieldType.TypeId);

        Assert.That(type, Is.InstanceOf<AddressLookupFieldType>());
        Assert.That(type!.Alias, Is.EqualTo("communityAddressLookup"));
    }

    [Test]
    public void ProxyController_CanBeBuilt_SoAValidTicketNoLongerGives500()
    {
        // M1 shipped the proxy without a resolver, so a valid-ticket POST failed to build the controller.
        Assert.That(Services.GetRequiredService<IAddressFieldResolver>(), Is.InstanceOf<FormsAddressFieldResolver>());
        Assert.That(ActivatorUtilities.CreateInstance<AddressLookupController>(Services), Is.Not.Null);
    }
}
