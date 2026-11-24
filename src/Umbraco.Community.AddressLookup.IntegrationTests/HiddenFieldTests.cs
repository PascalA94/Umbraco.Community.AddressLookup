using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Forms.Core.Enums;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Providers;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.AddressLookup.IntegrationTests;

/// <summary>
/// A field hidden by a condition, on its own or through its fieldset, reports no error and stores nothing. Runs against the
/// real Forms condition evaluation, field type storage and placeholder service of each line's host.
/// </summary>
public class HiddenFieldTests : ApiTestBase
{
    private static (Form Form, Field Address, Field Toggle) Build(bool hideFieldset)
    {
        var toggle = new Field { Id = Guid.NewGuid(), Settings = new Dictionary<string, string>(), Values = ["no"] };
        var address = new Field
        {
            Id = Guid.NewGuid(),
            Mandatory = true,
            FieldTypeId = AddressLookupFieldType.TypeId,
            Settings = new Dictionary<string, string>(),
        };
        var showWhenYes = new FieldCondition
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            ActionType = FieldConditionActionType.Show,
            LogicType = FieldConditionLogicType.All,
            Rules = [new FieldConditionRule { Id = Guid.NewGuid(), Field = toggle.Id, Operator = FieldConditionRuleOperator.Is, Value = "yes" }],
        };

        // Hiding at the fieldset leaves the field itself without a condition: the case Forms does not shield on every path.
        if (!hideFieldset)
        {
            address.Condition = showWhenYes;
        }

        var form = new Form
        {
            Pages =
            [
                new Page
                {
                    Id = Guid.NewGuid(),
                    FieldSets =
                    [
                        new FieldSet { Id = Guid.NewGuid(), Containers = [new FieldsetContainer { Fields = [toggle] }] },
                        new FieldSet
                        {
                            Id = Guid.NewGuid(),
                            Condition = hideFieldset ? showWhenYes : null,
                            Containers = [new FieldsetContainer { Fields = [address] }],
                        },
                    ],
                },
            ],
        };
        return (form, address, toggle);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Hidden_ReportsNoErrorsAndStoresNothing_ForAMandatoryField(bool hideFieldset)
    {
        var type = Services.GetRequiredService<FieldCollection>().OfType<AddressLookupFieldType>().Single();
        var (form, address, _) = Build(hideFieldset);
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues> { [$"{address.Id}.line1"] = "Flat 2" });

        var errors = type.ValidateField(
            form, address, [], context,
            Services.GetRequiredService<IPlaceholderParsingService>(), Services.GetRequiredService<IFieldTypeStorage>());

        Assert.That(errors, Is.Empty);
        Assert.That(type.ConvertToRecord(address, [], context), Is.Empty);
    }

    [Test]
    public void Visible_StillReportsAHalfFilledAddress()
    {
        var type = Services.GetRequiredService<FieldCollection>().OfType<AddressLookupFieldType>().Single();
        var (form, address, toggle) = Build(hideFieldset: true);
        toggle.Values = ["yes"];
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues> { [$"{address.Id}.line1"] = "Flat 2" });

        var errors = type.ValidateField(
            form, address, [], context,
            Services.GetRequiredService<IPlaceholderParsingService>(), Services.GetRequiredService<IFieldTypeStorage>());

        Assert.That(errors, Is.Not.Empty);
    }
}
