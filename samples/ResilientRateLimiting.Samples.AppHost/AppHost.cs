using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResilientRateLimiting.Samples.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
var weights = "even";
var replicaCount = 3;
var traffic = new DemoTraffic();

var redis = builder.AddRedis("redis").WithRedisInsight();

IResourceBuilder<ProjectResource> Api(string name) => builder.AddProject<Projects.ResilientRateLimiting_Samples_Web>(name)
    .WithHttpEndpoint()
    .WithReference(redis, "Redis")
    .WithReference(redis, "RedisSecondary")
    .WithEnvironment("RateLimits__PermitLimit", "30")
    .WithEnvironment("RateLimits__WindowSeconds", "10")
    .WithEnvironment(context => context.EnvironmentVariables["RateLimits__TypicalReplicaCount"] = replicaCount.ToString(CultureInfo.InvariantCulture))
    .WaitFor(redis);

IResourceBuilder<ProjectResource>[] apis = [Api("api-a"), Api("api-b"), Api("api-c")];

var gateway = builder.AddProject<Projects.ResilientRateLimiting_Samples_Gateway>("gateway")
    .WithHttpEndpoint()
    .WithEnvironment(context => context.EnvironmentVariables["Gateway__Weights"] = weights);
foreach (var api in apis)
{
    gateway.WithEnvironment($"Gateway__Destinations__{api.Resource.Name}", api.GetEndpoint("http")).WaitFor(api);
}

// Null until the gateway got its address; the CLI does not apply UpdateState, so commands check this themselves.
Uri? GatewayUrl() => gateway.GetEndpoint("http") is { IsAllocated: true } endpoint ? new(endpoint.Url) : null;

const string NoGateway = "The gateway is not running yet; start it and try again.";

static ResourceCommandState WhenRunning(UpdateCommandStateContext context) =>
    context.ResourceSnapshot.State?.Text == KnownResourceStates.Running ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;

// The CLI prints only success or failure, so the message also goes to the gateway's log.
ExecuteCommandResult Report(ExecuteCommandContext context, bool success, string message)
{
    context.Services.GetRequiredService<ResourceLoggerService>().GetLogger(gateway.Resource).LogInformation("{Message}", message);
    return success ? CommandResults.Success(message) : CommandResults.Failure(message);
}

// Aspire reports Running before the app listens, so wait for an answer: the next demo step may stop Redis.
async Task<ExecuteCommandResult> Restart(ExecuteCommandContext context, IEnumerable<ProjectResource> resources, string done)
{
    var commands = context.Services.GetRequiredService<ResourceCommandService>();
    foreach (var resource in resources)
    {
        var result = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.RestartCommand, context.CancellationToken);
        if (!result.Success)
        {
            return Report(context, false, $"Restarting {resource.Name} failed: {result.Message}");
        }

        if (!await traffic.WaitUntilAnswersAsync(new Uri(resource.GetEndpoint("http").Url), TimeSpan.FromSeconds(60), context.CancellationToken))
        {
            return Report(context, false, $"{resource.Name} did not answer within 60 seconds after the restart; see its log.");
        }
    }

    return Report(context, true, done);
}

gateway.WithCommand("start-load", "Start steady load", context => Task.FromResult(GatewayUrl() is not { } url
        ? Report(context, false, NoGateway)
        : traffic.StartSteady(url, 5)
            ? Report(context, true, "Steady load started: 5 requests per second as client 'steady'.")
            : Report(context, false, "Steady load is already running.")),
    new CommandOptions { Description = "5 requests per second, above the demo limit, so the charts show allowed and limited.", IconName = "Play", UpdateState = WhenRunning });

gateway.WithCommand("stop-load", "Stop steady load", context => Task.FromResult(traffic.StopSteady()
        ? Report(context, true, "Steady load stopped.")
        : Report(context, false, "Steady load is not running.")),
    new CommandOptions { IconName = "Stop", UpdateState = WhenRunning });

gateway.WithCommand("burst", "Send burst of 90", async context =>
    {
        if (GatewayUrl() is not { } url)
        {
            return Report(context, false, NoGateway);
        }

        var (allowed, limited, failed) = await traffic.BurstAsync(url, 90, context.CancellationToken);
        return allowed + limited == 0
            ? Report(context, false, $"No answer from the gateway (failed {failed}); is it running?")
            : Report(context, true, $"allowed {allowed}, limited {limited}, failed {failed}");
    },
    new CommandOptions { Description = "90 requests at once as a new client; with Redis up, the shared limit admits 30.", IconName = "Send", UpdateState = WhenRunning });

gateway.WithCommand("traffic-even", "Traffic: even", context => { weights = "even"; return Restart(context, [gateway.Resource], "Gateway restarted with even traffic."); },
    new CommandOptions { IconName = "ArrowSplit", UpdateState = WhenRunning });

gateway.WithCommand("traffic-uneven", "Traffic: 60/30/10", context => { weights = "60/30/10"; return Restart(context, [gateway.Resource], "Gateway restarted: api-a 60 %, api-b 30 %, api-c 10 %."); },
    new CommandOptions { IconName = "ArrowSplit", UpdateState = WhenRunning });

gateway.WithCommand("budget-equal", "Budget: equal share", context => { replicaCount = 3; return Restart(context, apis.Select(api => api.Resource), "APIs restarted: local budget 10 each."); },
    new CommandOptions { Description = "Restarts the three APIs. Use it while Redis is running: an API cannot start without Redis.", IconName = "ArrowReset", UpdateState = WhenRunning });

gateway.WithCommand("budget-2", "Budget: as if 2 replicas", context => { replicaCount = 2; return Restart(context, apis.Select(api => api.Resource), "APIs restarted: local budget 15 each."); },
    new CommandOptions { Description = "Restarts the three APIs. Use it while Redis is running: an API cannot start without Redis.", IconName = "ArrowUp", UpdateState = WhenRunning });

builder.Build().Run();
