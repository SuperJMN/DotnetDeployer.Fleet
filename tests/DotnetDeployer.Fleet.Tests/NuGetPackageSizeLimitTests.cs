using DotnetDeployer.Fleet.Core.Domain;
using FluentAssertions;

namespace DotnetDeployer.Fleet.Tests;

public sealed class NuGetPackageSizeLimitTests
{
    [Fact]
    public void Packages_up_to_the_NuGet_org_limit_are_accepted()
    {
        NuGetPackageSizeLimit.Check("Zafiro.Avalonia.ShowMe.0.1.0.nupkg", 250L * 1024 * 1024)
            .Should().BeNull();
    }

    [Fact]
    public void Oversized_packages_are_reported_with_name_size_and_limit()
    {
        NuGetPackageSizeLimit.Check("Huge.1.0.0.nupkg", 300L * 1024 * 1024)
            .Should().Be("NuGet package Huge.1.0.0.nupkg is 300 MB, over the 250 MB limit for release packages (the same limit NuGet.org applies).");
    }
}
