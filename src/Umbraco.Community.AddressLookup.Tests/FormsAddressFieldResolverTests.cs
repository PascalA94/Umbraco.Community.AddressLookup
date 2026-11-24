using NSubstitute;
using Umbraco.Cms.Core.Cache;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.AddressLookup.Tests;

public class FormsAddressFieldResolverTests
{
    private static readonly Guid FormId = Guid.NewGuid();
    private readonly IFormService _forms = Substitute.For<IFormService>();

    private static Form FormWith(params Field[] fields) => new()
    {
        Id = FormId,
        Pages = [new Page { FieldSets = [new FieldSet { Containers = [new FieldsetContainer { Fields = [.. fields] }] }] }],
    };

    private static Field Address(string? countries) => new()
    {
        Id = Guid.NewGuid(),
        FieldTypeId = AddressLookupFieldType.TypeId,
        Settings = countries is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Countries"] = countries },
    };

    private FormsAddressFieldResolver Create() => new(_forms, AppCaches.NoCache);

    [Fact]
    public void Resolves_CountriesFromTheFieldSetting()
    {
        var field = Address("gb, ie");
        _forms.Get(FormId).Returns(FormWith(field));

        Assert.Equal(["GB", "IE"], Create().Resolve(FormId, field.Id)!.Countries);
    }

    [Fact]
    public void NoCountriesSetting_DefaultsToGb()
    {
        var field = Address(null);
        _forms.Get(FormId).Returns(FormWith(field));

        Assert.Equal(["GB"], Create().Resolve(FormId, field.Id)!.Countries);
    }

    [Fact]
    public void UnknownForm_Null()
    {
        _forms.Get(FormId).Returns((Form?)null);

        Assert.Null(Create().Resolve(FormId, Guid.NewGuid()));
    }

    [Fact]
    public void FieldNotOnForm_Null()
    {
        _forms.Get(FormId).Returns(FormWith(Address("GB")));

        Assert.Null(Create().Resolve(FormId, Guid.NewGuid()));
    }

    [Fact]
    public void FieldOfAnotherType_Null()
    {
        var other = Address("GB");
        other.FieldTypeId = Guid.NewGuid();
        _forms.Get(FormId).Returns(FormWith(other));

        Assert.Null(Create().Resolve(FormId, other.Id));
    }

    [Fact]
    public void Result_IsCachedForAMinute()
    {
        var field = Address("GB");
        _forms.Get(FormId).Returns(FormWith(field));
        var resolver = new FormsAddressFieldResolver(_forms, new AppCaches(
            new DeepCloneAppCache(new ObjectCacheAppCache()), NoAppCache.Instance, new IsolatedCaches(_ => new ObjectCacheAppCache())));

        resolver.Resolve(FormId, field.Id);
        resolver.Resolve(FormId, field.Id);

        _forms.Received(1).Get(FormId);
    }
}
