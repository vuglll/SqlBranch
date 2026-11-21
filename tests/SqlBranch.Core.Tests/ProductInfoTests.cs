using SqlBranch.Core;

namespace SqlBranch.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void Version_is_the_package_version_without_a_source_suffix()
    {
        Assert.Matches(@"^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$", ProductInfo.Version);
    }
}
