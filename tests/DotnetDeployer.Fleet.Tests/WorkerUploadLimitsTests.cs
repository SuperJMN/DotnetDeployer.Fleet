using DotnetDeployer.Fleet.Coordinator.Endpoints;
using DotnetDeployer.Fleet.Coordinator.Services;
using DotnetDeployer.Fleet.Core.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DotnetDeployer.Fleet.Tests;

public sealed class WorkerUploadLimitsTests
{
    [Theory]
    [InlineData("/api/queue/jobs/{id:guid}/artifacts", 512L * 1024 * 1024)]
    [InlineData("/api/queue/jobs/{id:guid}/nuget-release/{commitSha}/packages/{packageId}", 64L * 1024 * 1024)]
    public async Task WorkerUploadEndpoints_UseAppropriateBodyLimits(string route, long expectedLimit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Substitute.For<IFleetStorage>());
        builder.Services.AddSingleton(Substitute.For<IDurationEstimator>());
        builder.Services.AddSingleton<LogBroadcaster>();
        builder.Services.AddSingleton<JobAssignmentSignal>();
        builder.Services.AddSingleton(new PackageArtifactStore(Path.GetTempPath()));
        builder.Services.AddSingleton(new NuGetReleaseStore(Path.GetTempPath()));
        await using var app = builder.Build();
        app.MapJobEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == route
                                && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

        endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize
            .Should().Be(expectedLimit);
    }
}
