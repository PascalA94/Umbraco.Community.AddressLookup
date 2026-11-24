using Umbraco.Cms.Core.Cache;
using Umbraco.Community.AddressLookup.Controllers;
using Umbraco.Extensions;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.AddressLookup.FieldTypes;

/// <summary>
/// Reads the lookup field from Forms for the proxy (design 5.2): the field must exist on the form and be this field type, and
/// its <c>Countries</c> setting is what a search may use. Read-only, so the proxy never holds a Forms write service (pitfall P5).
/// </summary>
public sealed class FormsAddressFieldResolver(IFormService forms, AppCaches caches) : IAddressFieldResolver
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public AddressFieldInfo? Resolve(Guid formId, Guid fieldId)
    {
        // A ticket is one page view and a page can fire many Finds, so this is hit often. The minute is the longest a removed
        // field or a changed Countries setting keeps working for. Misses are not cached: nothing is stored for an unknown id.
        var key = $"community.addressLookup.field.{formId:N}.{fieldId:N}";
        return caches.RuntimeCache.GetCacheItem(key, () => Load(formId, fieldId), CacheFor);
    }

    private AddressFieldInfo? Load(Guid formId, Guid fieldId)
    {
        var field = forms.Get(formId)?.AllFields.FirstOrDefault(f => f.Id == fieldId && f.FieldTypeId == AddressLookupFieldType.TypeId);
        return field is null
            ? null
            : new AddressFieldInfo(AddressFieldSettings.ParseCountries(AddressFieldSettings.Get(field.Settings, "Countries")));
    }
}
