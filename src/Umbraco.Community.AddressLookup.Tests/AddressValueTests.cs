using Umbraco.Community.AddressLookup.Model;

namespace Umbraco.Community.AddressLookup.Tests;

public class AddressValueTests
{
    private const string SpecExample =
        "{\"v\":1,\"lines\":[\"Flat 2\",\"10 High Street\"],\"town\":\"Leeds\",\"county\":null,\"postcode\":\"LS1 4AP\"," +
        "\"countryIso2\":\"GB\",\"country\":\"United Kingdom\",\"provider\":\"loqate\",\"providerId\":\"GB|RM|A|123\",\"source\":\"lookup\"}";

    private static AddressValue Sample() => new(
        ["Flat 2", "10 High Street"], "Leeds", null, "LS1 4AP", "GB", "United Kingdom", "loqate", "GB|RM|A|123", "lookup");

    [Fact]
    public void Json_RoundTrip_MatchesSpecShape()
    {
        var json = Sample().ToJson();

        Assert.Equal(SpecExample, json);
        Assert.True(AddressValue.TryParse(json, out var parsed));
        Assert.Equal(json, parsed!.ToJson());
    }

    [Fact]
    public void Json_NonAscii_IsStoredReadably()
    {
        var value = Sample() with { Lines = ["Ynys Môn"] };

        var json = value.ToJson();

        Assert.Contains("Ynys Môn", json);
        Assert.True(AddressValue.TryParse(json, out var parsed));
        Assert.Equal("Ynys Môn", parsed!.Lines[0]);
    }

    [Fact]
    public void Json_HtmlSensitiveCharacters_AreStillEscaped()
    {
        var json = (Sample() with { Town = "<b>&" }).ToJson();

        Assert.DoesNotContain("<", json);
        Assert.DoesNotContain("&", json);
    }

    [Theory]
    [InlineData("ls14ap", "LS1 4AP")]
    [InlineData("sw1a1aa", "SW1A 1AA")]
    [InlineData("  m1   1ae ", "M1 1AE")]
    [InlineData("EC1A 1BB", "EC1A 1BB")]
    public void Postcode_Normalises(string input, string expected)
    {
        var value = Sample() with { Postcode = input };

        Assert.Equal(expected, value.Normalise().Postcode);
    }

    [Fact]
    public void Normalise_TrimsCollapsesAndDropsEmptyLines()
    {
        var value = Sample() with { Lines = ["  Flat   2 ", "", "   ", "10  High Street"], Town = " Leeds  ", County = "  " };

        var normalised = value.Normalise();

        Assert.Equal(["Flat 2", "10 High Street"], normalised.Lines);
        Assert.Equal("Leeds", normalised.Town);
        Assert.Null(normalised.County);
    }

    [Fact]
    public void Normalise_LeavesNonGbPostcodeCase()
    {
        var value = Sample() with { CountryIso2 = "ie", Postcode = " d02  af30 " };

        var normalised = value.Normalise();

        Assert.Equal("IE", normalised.CountryIso2);
        Assert.Equal("d02 af30", normalised.Postcode);
    }

    [Fact]
    public void Format_OneLine()
    {
        Assert.Equal("Flat 2, 10 High Street, Leeds, LS1 4AP, United Kingdom", Sample().Format());
        Assert.Equal(
            "10 High Street, Leeds, West Yorkshire, LS1 4AP",
            (Sample() with { Lines = ["10 High Street"], County = "West Yorkshire", Country = null }).Format());
    }

    [Theory]
    [InlineData("{\"v\":2,\"lines\":[],\"town\":\"x\",\"county\":null,\"postcode\":\"x\",\"countryIso2\":\"GB\",\"country\":null,\"provider\":null,\"providerId\":null,\"source\":\"manual\"}")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("null")]
    public void TryParse_RejectsUnknownVersionAndGarbage(string json)
    {
        Assert.False(AddressValue.TryParse(json, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void TryParse_RejectsOversizeInput()
    {
        Assert.False(AddressValue.TryParse(new string(' ', 10_000) + SpecExample, out _));
    }

    [Fact]
    public void ToString_PrintsNoAddressData()
    {
        var value = Sample() with { Lines = ["1 Sentinel Street"], Town = "Sentinelton", ProviderId = "SENTINEL-ID" };

        foreach (var text in new[]
        {
            value.ToString(),
            new AddressQuery("SENTINEL QUERY", "SENTINEL-CONTAINER", ["GB"], 8).ToString(),
            new AddressSuggestion("SENTINEL-ID", "1 Sentinel Street", "Sentinelton", false).ToString(),
        })
        {
            Assert.DoesNotContain("entinel", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LS1", text);
        }
    }

    [Fact]
    public void FitToField_FoldsFourLinesIntoThree_AndCapsLengths()
    {
        var value = Sample() with { Lines = ["Acme Ltd", "Unit 4", "Mill Lane", "Kirkstall"], Town = new string('t', 80) };

        var fitted = value.FitToField();

        Assert.Equal(["Acme Ltd", "Unit 4", "Mill Lane, Kirkstall"], fitted.Lines);
        Assert.Equal(60, fitted.Town.Length);
        Assert.Equal(Sample().Lines, Sample().FitToField().Lines);
    }

    [Fact]
    public void Wire_ToString_PrintsNoAddressData()
    {
        var wire = new AddressValue.Wire(1, ["1 Sentinel Street"], "Sentinelton", null, "LS1 4AP", "GB", null, null, "SENTINEL-ID", "lookup");

        Assert.DoesNotContain("entinel", wire.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
