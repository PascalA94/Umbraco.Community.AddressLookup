using System.Collections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.FieldTypes;
using Umbraco.Community.AddressLookup.Model;
using Umbraco.Community.AddressLookup.Providers;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Tests.Support;
using Umbraco.Forms.Core.Enums;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.AddressLookup.Tests;

/// <summary>
/// A field the visitor cannot see must not block the submit or store anything. Forms hides a field through its own condition
/// or its fieldset's (a page has no condition; only its buttons do), and runs <c>ValidateField</c> for a hidden field too
/// on the Delivery API and the Management API, so the type has to decide visibility itself.
/// </summary>
public class FieldTypeVisibilityTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
    private readonly Field _toggle = new() { Id = Guid.NewGuid(), Settings = new Dictionary<string, string>(), Values = ["no"] };
    private readonly Field _field = new() { Id = Guid.NewGuid(), Settings = new Dictionary<string, string>() };
    private readonly IFieldTypeStorage _storage = Substitute.For<IFieldTypeStorage>();
    private readonly IPlaceholderParsingService _placeholders = Substitute.For<IPlaceholderParsingService>();
    private readonly AddressLookupFieldType _type;

    public FieldTypeVisibilityTests()
    {
        _placeholders
            .ParsePlaceHolders(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Umbraco.Forms.Core.Persistence.Dtos.Record?>(), Arg.Any<Form?>(), Arg.Any<Hashtable?>(), Arg.Any<IDictionary<string, string?>?>())
            .Returns(call => call.Arg<string>());

        var provider = Substitute.For<IAddressLookupProvider>();
        provider.Alias.Returns("stub");
        var proofs = new AddressProofService(DataProtectionProvider.Create("address-lookup-tests"), _time);
        _type = new AddressLookupFieldType(
            new TestOptionsMonitor<AddressLookupOptions>(new AddressLookupOptions { Provider = ProviderKind.Stub }), proofs, [provider]);
    }

    // Visible only while the toggle field says "yes". The toggle says "no" unless a test turns it on.
    private FieldCondition ShowWhenToggleIsYes() => new()
    {
        Id = Guid.NewGuid(),
        Enabled = true,
        ActionType = FieldConditionActionType.Show,
        LogicType = FieldConditionLogicType.All,
        Rules = [new FieldConditionRule { Id = Guid.NewGuid(), Field = _toggle.Id, Operator = FieldConditionRuleOperator.Is, Value = "yes" }],
    };

    private Form BuildForm(bool fieldHidden, bool fieldsetHidden)
    {
        _field.Condition = fieldHidden ? ShowWhenToggleIsYes() : null;
        var togglePart = new FieldSet { Id = Guid.NewGuid(), Containers = [new FieldsetContainer { Fields = [_toggle] }] };
        var addressPart = new FieldSet
        {
            Id = Guid.NewGuid(),
            Condition = fieldsetHidden ? ShowWhenToggleIsYes() : null,
            Containers = [new FieldsetContainer { Fields = [_field] }],
        };

        return new Form { Pages = [new Page { Id = Guid.NewGuid(), FieldSets = [togglePart, addressPart] }] };
    }

    private HttpContext HalfFilledPost()
    {
        var parts = new Dictionary<string, string> { ["line1"] = "Flat 2", ["country"] = "GB" };
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(parts.ToDictionary(p => $"{_field.Id}.{p.Key}", p => new StringValues(p.Value)));
        return context;
    }

    private List<string> Validate(Form form, HttpContext context, bool mandatory)
    {
        _field.Mandatory = mandatory;
        return _type.ValidateField(form, _field, [], context, _placeholders, _storage).ToList();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public void HiddenField_ReportsNoErrors_MandatoryOrNot(bool mandatory, bool fieldHidden, bool fieldsetHidden)
    {
        var form = BuildForm(fieldHidden, fieldsetHidden);

        Assert.Empty(Validate(form, HalfFilledPost(), mandatory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VisibleField_StillReportsAHalfFilledAddress(bool mandatory)
    {
        _toggle.Values = ["yes"];
        var form = BuildForm(fieldHidden: true, fieldsetHidden: false);

        Assert.Equal(["Enter a town or city", "Enter a postcode"], Validate(form, HalfFilledPost(), mandatory));
    }

    [Fact]
    public void ConditionNotMet_ButFieldInAVisibleFieldset_ReportsMissingParts()
    {
        var form = BuildForm(fieldHidden: false, fieldsetHidden: false);

        Assert.NotEmpty(Validate(form, HalfFilledPost(), mandatory: true));
    }

    [Fact]
    public void HiddenField_StoresNothing_EvenWhenTheValueIsComplete()
    {
        var form = BuildForm(fieldHidden: true, fieldsetHidden: false);
        var complete = new AddressValue(["1 High Street"], "Leeds", null, "LS1 4AP", "GB", null, null, null, "manual").ToJson();
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";

        // The Delivery API order: validate, then store whatever ConvertToRecord returns.
        Assert.Empty(_type.ValidateField(form, _field, [complete], context, _placeholders, _storage));
        Assert.Empty(_type.ConvertToRecord(_field, [complete], context));
    }

    [Fact]
    public void HiddenField_StoresNothing_AfterProcessSubmittedValueAlreadyAcceptedIt()
    {
        // The form path runs ProcessSubmittedValue first; hiding is only known when ValidateField runs.
        var form = BuildForm(fieldHidden: false, fieldsetHidden: true);
        var context = HalfFilledPost();
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            [$"{_field.Id}.line1"] = "1 High Street", [$"{_field.Id}.town"] = "Leeds", [$"{_field.Id}.postcode"] = "LS1 4AP", [$"{_field.Id}.country"] = "GB",
        });

        Assert.Single(_type.ProcessSubmittedValue(_field, [], context));
        Assert.Empty(_type.ValidateField(form, _field, [], context, _placeholders, _storage));
        Assert.Empty(_type.ConvertToRecord(_field, [], context));
    }
}
