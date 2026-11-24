using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Forms.Core.Enums;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Providers;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.AddressLookup.DemoSite.DemoForms;

/// <summary>Demo only: three fixed forms and a page that renders them, so the e2e spec and a person have something to open.</summary>
public static class DemoFormIds
{
    public static readonly Guid Single = new("0a1b2c3d-0001-4000-8000-000000000001");
    public static readonly Guid TwoPages = new("0a1b2c3d-0002-4000-8000-000000000002");
    public static readonly Guid Conditional = new("0a1b2c3d-0003-4000-8000-000000000003");

    /// <summary>The page names double as their URL segments (<c>/address-demo-single</c> and so on).</summary>
    public static Guid? Find(string pageName) => pageName switch
    {
        "address-demo-single" => Single,
        "address-demo-two-pages" => TwoPages,
        "address-demo-conditional" => Conditional,
        _ => null,
    };
}

public sealed class DemoFormsComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoFormSeeder>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoPageSeeder>();
    }
}

public sealed class DemoFormSeeder(IFormService forms, FieldCollection fields, ILogger<DemoFormSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            // By class name: the short answer type's alias and display name differ between Forms lines.
            var text = fields.FirstOrDefault(f => f.GetType().Name == "Textfield")
                ?? throw new InvalidOperationException($"The Forms short answer field type is missing ({fields.Count()} field types are registered).");

            Seed(DemoFormIds.Single, "Address demo", [[Address(Guid.NewGuid(), mandatory: true)]], text.Id);
            Seed(
                DemoFormIds.TwoPages, "Address demo, two pages",
                [[Address(Guid.NewGuid(), mandatory: true)], [Text(Guid.NewGuid(), "Your name", "name", text.Id, mandatory: false)]],
                text.Id);

            var delivery = Guid.NewGuid();
            var conditional = Address(Guid.NewGuid(), mandatory: true);
            conditional.Condition = new FieldCondition
            {
                Enabled = true,
                ActionType = FieldConditionActionType.Show,
                LogicType = FieldConditionLogicType.All,
                Rules = [new FieldConditionRule { Field = delivery, Operator = FieldConditionRuleOperator.Is, Value = "post" }],
            };
            // A mandatory built-in field under the same condition: if it blocks the submit, the condition setup is at fault, not the address field.
            var control = Text(Guid.NewGuid(), "Delivery note", "note", text.Id, mandatory: true);
            control.Condition = new FieldCondition
            {
                Enabled = true,
                ActionType = FieldConditionActionType.Show,
                LogicType = FieldConditionLogicType.All,
                Rules = [new FieldConditionRule { Field = delivery, Operator = FieldConditionRuleOperator.Is, Value = "post" }],
            };
            Seed(DemoFormIds.Conditional, "Address demo, conditional", [[Text(delivery, "Delivery method", "delivery", text.Id, mandatory: false), conditional, control]], text.Id);
        }
        catch (Exception ex)
        {
            // The demo still boots without its forms; the e2e spec fails with a clear "form not found" instead.
            logger.LogError(ex, "Could not seed the demo forms");
        }

        return Task.CompletedTask;
    }

    private static Field Address(Guid id, bool mandatory) => new()
    {
        Id = id,
        Caption = "Home address",
        Alias = "homeAddress",
        FieldTypeId = AddressLookupFieldType.TypeId,
        Mandatory = mandatory,
        Settings = new Dictionary<string, string>
        {
            ["Countries"] = "GB",
            ["Placeholder"] = "Postcode or address",
            ["AllowManualEntry"] = "true",
            ["RequireLookup"] = "false",
            ["ShowCountry"] = "false",
        },
    };

    private static Field Text(Guid id, string caption, string alias, Guid fieldTypeId, bool mandatory) => new()
    {
        Id = id,
        Caption = caption,
        Alias = alias,
        FieldTypeId = fieldTypeId,
        Mandatory = mandatory,
        Settings = new Dictionary<string, string>(),
    };

    // Each inner list is one page; every page is one fieldset with one full-width container.
    private void Seed(Guid id, string name, List<List<Field>> pages, Guid textTypeId)
    {
        if (forms.Get(id) is not null)
        {
            return;
        }

        var form = new Form
        {
            Id = id,
            Name = name,
            MessageOnSubmit = "Thanks, we have your details.",
            StoreRecordsLocally = true,
            SubmitLabel = "Send",
            NextLabel = "Next",
            PrevLabel = "Back",
            Pages = [],
        };

        for (var p = 0; p < pages.Count; p++)
        {
            var page = new Page { Id = Guid.NewGuid(), Form = id, Caption = $"Page {p + 1}", SortOrder = p, FieldSets = [] };
            var fieldSet = new FieldSet { Id = Guid.NewGuid(), Page = page.Id, Caption = string.Empty, SortOrder = 0, Containers = [] };
            fieldSet.Containers.Add(new FieldsetContainer { Id = Guid.NewGuid(), Width = 12, Fields = pages[p] });
            page.FieldSets.Add(fieldSet);
            form.Pages.Add(page);
        }

        forms.Insert(form, null, addPermissions: false);
    }
}
