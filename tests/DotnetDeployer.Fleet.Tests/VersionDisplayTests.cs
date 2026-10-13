using DotnetDeployer.Fleet.App.ViewModels;

namespace DotnetDeployer.Fleet.Tests;

public class VersionDisplayTests
{
    [Theory]
    [InlineData("0.1.9+58f1c495e00554b14859456e8557aa98b9b177dd", "0.1.9")]
    [InlineData("0.1.10-1+Branch.master.Sha.93bf16a3c8799dffeec6a642f430b3961c1f037e", "0.1.10-1")]
    [InlineData("2.0.1-rc.1", "2.0.1-rc.1")]
    [InlineData("2.0.1", "2.0.1")]
    [InlineData(null, null)]
    public void Visible_trims_semver_build_metadata(string? version, string? expected)
    {
        VersionDisplay.Visible(version).Should().Be(expected);
    }

    [Fact]
    public void VisibleWithPrefix_keeps_prefix_out_of_empty_versions()
    {
        VersionDisplay.VisibleWithPrefix(null).Should().BeNull();
        VersionDisplay.VisibleWithPrefix("").Should().BeEmpty();
        VersionDisplay.VisibleWithPrefix("1.2.3+abcdef").Should().Be("v1.2.3");
    }

    [Theory]
    [InlineData("0.1.32+f0993c19", "0.1.38+d1c1afdb", true)]
    [InlineData("0.1.38+f0993c19", "0.1.38+d1c1afdb", false)]
    [InlineData("0.1.38", "0.1.38", false)]
    [InlineData("0.1.38", null, false)]
    [InlineData(null, "0.1.38", false)]
    [InlineData("", "0.1.38", false)]
    public void Differ_reports_only_known_versions_that_differ(string? version, string? other, bool expected)
    {
        VersionDisplay.Differ(version, other).Should().Be(expected);
    }
}
