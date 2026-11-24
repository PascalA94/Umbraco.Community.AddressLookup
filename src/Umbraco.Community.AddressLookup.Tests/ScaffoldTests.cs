using System.Reflection;
using Umbraco.Forms.Core;

namespace Umbraco.Community.AddressLookup.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Assembly_TargetsSelectedMajor()
    {
        var major = typeof(PackageConstants).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "UmbracoMajor")?.Value;

        Assert.False(string.IsNullOrEmpty(major), "AssemblyMetadata(\"UmbracoMajor\") is missing");
        Assert.Equal(major, typeof(FieldType).Assembly.GetName().Version!.Major.ToString());
    }
}
