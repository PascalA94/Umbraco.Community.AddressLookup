using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Umbraco.Community.AddressLookup.DemoSite.DemoForms;

/// <summary>
/// Demo only: three published pages, one per demo form. Forms posts back to the page it was rendered on and re-renders it with
/// the result, so the form has to live on real Umbraco content for submit, validation errors and the thank-you message to work.
/// </summary>
public sealed class DemoPageSeeder(
    ITemplateService templates, IContentTypeService contentTypes, IContentService content, IShortStringHelper shortStrings,
    ILogger<DemoPageSeeder> logger) : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string Alias = "addressDemoPage";

    private const string TemplateBody = """
        @using Umbraco.Cms.Web.Common.Views
        @inherits UmbracoViewPage
        @addTagHelper *, Umbraco.Forms.Web
        @{
            Layout = null;
            var formId = global::Umbraco.Community.AddressLookup.DemoSite.DemoForms.DemoFormIds.Find(Model.Name);
        }
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Address lookup demo</title>
            <script src="/App_Plugins/UmbracoForms/assets/aspnet-client-validation/dist/aspnet-validation.min.js"></script>
        </head>
        <body>
        <main>
            <h1>Address lookup demo</h1>
            @if (formId is { } id)
            {
                <umb-forms-render form-id="@id" theme="default" exclude-scripts="true" />
            }
        </main>
            <umb-forms-render-scripts />
        </body>
        </html>
        """;

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            if (content.GetRootContent().Any(c => c.ContentType.Alias == Alias))
            {
                return;
            }

            var template = await templates.GetAsync(Alias);
            if (template is null)
            {
                var created = await templates.CreateAsync("Address demo page", Alias, TemplateBody, Cms.Core.Constants.Security.SuperUserKey);
                template = created.Result;
            }

            var type = contentTypes.Get(Alias);
            if (type is null)
            {
                type = new ContentType(shortStrings, -1)
                {
                    Alias = Alias,
                    Name = "Address demo page",
                    AllowedAsRoot = true,
                    Variations = ContentVariation.Nothing,
                };
                type.AllowedTemplates = [template!];
                type.SetDefaultTemplate(template!);
                await contentTypes.CreateAsync(type, Cms.Core.Constants.Security.SuperUserKey);
            }

            foreach (var name in new[] { "address-demo-single", "address-demo-two-pages", "address-demo-conditional", "address-demo-required" })
            {
                var page = content.Create(name, Cms.Core.Constants.System.Root, Alias);
                content.Save(page);
                content.Publish(page, ["*"]);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not seed the demo pages");
        }
    }
}
