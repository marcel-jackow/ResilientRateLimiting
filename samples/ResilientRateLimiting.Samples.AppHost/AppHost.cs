using System.Globalization;
using ResilientRateLimiting.Samples.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var settings = new DemoSettings();

var redis = builder.AddRedis("redis").WithRedisInsight();

// A profile without ports: three copies of one project need their own, and Visual Studio needs a profile to start them.
IResourceBuilder<ProjectResource> Api(string name) => builder.AddProject<Projects.ResilientRateLimiting_Samples_Web>(name, launchProfileName: "default")
    .WithHttpEndpoint()
    .WithReference(redis, "Redis")
    .WithReference(redis, "RedisSecondary")
    // One log line per request drowns the store failures the demo is about.
    .WithEnvironment("Logging__LogLevel__Microsoft.AspNetCore", "Warning")
    // Demo only: right after a start the first Redis calls take longer than the sample's 20 ms.
    .WithEnvironment("ResilientRateLimiting__Store__StoreTimeout", "00:00:00.200")
    .WithEnvironment("RateLimits__PermitLimit", "30")
    .WithEnvironment("RateLimits__WindowSeconds", "10")
    .WithEnvironment(context => context.EnvironmentVariables["RateLimits__TypicalReplicaCount"] = settings.ReplicaCount.ToString(CultureInfo.InvariantCulture))
    .WithEnvironment(context => context.EnvironmentVariables["RateLimits__Mode"] = settings.Mode)
    .WaitFor(redis);

IResourceBuilder<ProjectResource>[] apis = [Api("api-a"), Api("api-b"), Api("api-c")];

var gateway = builder.AddProject<Projects.ResilientRateLimiting_Samples_Gateway>("gateway", launchProfileName: "default")
    .WithHttpEndpoint()
    .WithEnvironment(context => context.EnvironmentVariables["Gateway__Weights"] = settings.Weights);
foreach (var api in apis)
{
    gateway.WithEnvironment($"Gateway__Destinations__{api.Resource.Name}", api.GetEndpoint("http")).WaitFor(api);
}

DemoCommands.AddTo(gateway, apis, redis, settings);

builder.Build().Run();
