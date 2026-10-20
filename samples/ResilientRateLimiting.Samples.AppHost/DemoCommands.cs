using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ResilientRateLimiting.Samples.AppHost;

/// <summary>The dashboard commands on the gateway resource: demo traffic and the settings switches.</summary>
internal sealed class DemoCommands
{
    private const string NoGateway = "The gateway is not running yet; start it and try again.";
    private const string NoRedis = "Redis is not running. Start it first (4.1 Start Redis): an API cannot start without Redis. Nothing was changed.";
    private const string NeedsRedis = "Restarts the three APIs. Use it while Redis is running: an API cannot start without Redis.";

    // Not rate limited, so checking that a restarted app answers records no lease.
    private const string RestartCheckPath = "/restart-check";

    private readonly IResourceBuilder<ProjectResource> _gateway;
    private readonly IReadOnlyList<IResourceBuilder<ProjectResource>> _apis;
    private readonly IResourceBuilder<RedisResource> _redis;
    private readonly DemoSettings _settings;
    private readonly DemoTraffic _traffic = new();

    private DemoCommands(IResourceBuilder<ProjectResource> gateway, IReadOnlyList<IResourceBuilder<ProjectResource>> apis, IResourceBuilder<RedisResource> redis, DemoSettings settings)
    {
        _gateway = gateway;
        _apis = apis;
        _redis = redis;
        _settings = settings;
    }

    // The menu is the demo script: scene.step, in the order they are clicked; the dashboard keeps this order.
    public static void AddTo(IResourceBuilder<ProjectResource> gateway, IReadOnlyList<IResourceBuilder<ProjectResource>> apis, IResourceBuilder<RedisResource> redis, DemoSettings settings)
    {
        var commands = new DemoCommands(gateway, apis, redis, settings);
        const string Burst = "90 requests as a new client, one every 10 ms.";

        gateway.WithCommand("burst-1", "1.1 Send burst of 90", commands.BurstAsync, Options(Burst + " In memory: 90 allowed.", "Send"));
        gateway.WithCommand("limiter-redis", "2.1 Switch to shared Redis", context => commands.SetModeAsync(context, "Resilient", "APIs restarted: one shared counter in Redis."),
            Options(NeedsRedis, "Database"));
        gateway.WithCommand("burst-2", "2.2 Send burst of 90", commands.BurstAsync, Options(Burst + " Shared Redis: 30 allowed.", "Send"));
        gateway.WithCommand("start-load", "3.1 Start steady load", commands.StartLoad,
            Options("5 requests per second, above the demo limit, so the charts show allowed and limited.", "Play"));
        gateway.WithCommand("redis-stop", "3.2 Stop Redis", context => commands.RedisAsync(context, KnownResourceCommands.StopCommand, "Redis stopped."),
            Options("Same as Stop on the redis resource.", "Stop"));
        gateway.WithCommand("burst-3", "3.3 Send burst of 90", commands.BurstAsync, Options(Burst + " Redis down: still 30, 10 from each local budget.", "Send"));
        gateway.WithCommand("redis-start", "4.1 Start Redis", context => commands.RedisAsync(context, KnownResourceCommands.StartCommand, "Redis started."),
            Options("Same as Start on the redis resource.", "Play"));
        gateway.WithCommand("stop-load", "4.2 Stop steady load", commands.StopLoad, Options(null, "Stop"));

        gateway.WithCommand("limiter-memory", "Limiter: in-memory", context => commands.SetModeAsync(context, "InMemory", "APIs restarted: each API counts on its own (in memory)."),
            Options(NeedsRedis, "Server"));
        gateway.WithCommand("traffic-even", "Traffic: even", context => commands.SetWeightsAsync(context, "even", "Gateway restarted with even traffic."),
            Options(null, "ArrowSplit"));
        gateway.WithCommand("traffic-uneven", "Traffic: 60/30/10", context => commands.SetWeightsAsync(context, "60/30/10", "Gateway restarted: api-a 60 %, api-b 30 %, api-c 10 %."),
            Options(null, "ArrowSplit"));
        gateway.WithCommand("budget-equal", "Budget: equal share", context => commands.SetReplicaCountAsync(context, 3, "APIs restarted: local budget 10 each."),
            Options(NeedsRedis, "ArrowReset"));
        gateway.WithCommand("budget-2", "Budget: as if 2 replicas", context => commands.SetReplicaCountAsync(context, 2, "APIs restarted: local budget 15 each."),
            Options(NeedsRedis, "ArrowUp"));
    }

    private static CommandOptions Options(string? description, string icon) =>
        new() { Description = description, IconName = icon, UpdateState = WhenRunning };

    private static ResourceCommandState WhenRunning(UpdateCommandStateContext context) =>
        context.ResourceSnapshot.State?.Text == KnownResourceStates.Running ? ResourceCommandState.Enabled : ResourceCommandState.Disabled;

    private Task<ExecuteCommandResult> StartLoad(ExecuteCommandContext context) => Task.FromResult(
        GatewayUrl() is not { } url ? Report(context, false, NoGateway)
        : _traffic.StartSteady(url, 5) ? Report(context, true, "Steady load started: 5 requests per second as client 'steady'.")
        : Report(context, false, "Steady load is already running."));

    private Task<ExecuteCommandResult> StopLoad(ExecuteCommandContext context) => Task.FromResult(
        _traffic.StopSteady() ? Report(context, true, "Steady load stopped.")
        : Report(context, false, "Steady load is not running."));

    private async Task<ExecuteCommandResult> BurstAsync(ExecuteCommandContext context)
    {
        if (GatewayUrl() is not { } url)
        {
            return Report(context, false, NoGateway);
        }

        var (allowed, limited, failed) = await _traffic.BurstAsync(url, 90, context.CancellationToken);
        return allowed + limited == 0
            ? Report(context, false, $"No answer from the gateway (failed {failed}); is it running?")
            : Report(context, true, $"allowed {allowed}, limited {limited}, failed {failed}");
    }

    private Task<ExecuteCommandResult> SetWeightsAsync(ExecuteCommandContext context, string weights, string done)
    {
        _settings.Weights = weights;
        return RestartAsync(context, [_gateway.Resource], done);
    }

    private async Task<ExecuteCommandResult> RedisAsync(ExecuteCommandContext context, string command, string done)
    {
        var result = await context.Services.GetRequiredService<ResourceCommandService>()
            .ExecuteCommandAsync(_redis.Resource, command, context.CancellationToken);
        return result.Success ? Report(context, true, done) : Report(context, false, $"Redis: {result.Message}");
    }

    private async Task<ExecuteCommandResult> SetModeAsync(ExecuteCommandContext context, string mode, string done)
    {
        if (!await RedisRunningAsync(context))
        {
            return Report(context, false, NoRedis);
        }

        _settings.Mode = mode;
        return await RestartAsync(context, _apis.Select(api => api.Resource), done);
    }

    private async Task<ExecuteCommandResult> SetReplicaCountAsync(ExecuteCommandContext context, int replicaCount, string done)
    {
        if (!await RedisRunningAsync(context))
        {
            return Report(context, false, NoRedis);
        }

        _settings.ReplicaCount = replicaCount;
        return await RestartAsync(context, _apis.Select(api => api.Resource), done);
    }

    // An API cannot start without Redis, so check before changing a setting or restarting anything.
    private async Task<bool> RedisRunningAsync(ExecuteCommandContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await context.Services.GetRequiredService<ResourceNotificationService>()
                .WaitForResourceAsync(_redis.Resource.Name, KnownResourceStates.Running, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    // Aspire reports Running before the app listens, so wait for an answer: the next demo step may stop Redis.
    private async Task<ExecuteCommandResult> RestartAsync(ExecuteCommandContext context, IEnumerable<ProjectResource> resources, string done)
    {
        var commands = context.Services.GetRequiredService<ResourceCommandService>();
        foreach (var resource in resources)
        {
            var result = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.RestartCommand, context.CancellationToken);
            if (!result.Success)
            {
                return Report(context, false, $"Restarting {resource.Name} failed: {result.Message}");
            }

            var check = new Uri(new Uri(resource.GetEndpoint("http").Url), RestartCheckPath);
            if (!await _traffic.WaitUntilAnswersAsync(check, TimeSpan.FromSeconds(60), context.CancellationToken))
            {
                return Report(context, false, $"{resource.Name} did not answer within 60 seconds after the restart; see its log.");
            }
        }

        return Report(context, true, done);
    }

    // Null until the gateway got its address; the CLI does not apply UpdateState, so commands check this themselves.
    private Uri? GatewayUrl() => _gateway.GetEndpoint("http") is { IsAllocated: true } endpoint ? new(endpoint.Url) : null;

    // The CLI prints only success or failure, so the message also goes to the gateway's log.
    private ExecuteCommandResult Report(ExecuteCommandContext context, bool success, string message)
    {
        context.Services.GetRequiredService<ResourceLoggerService>().GetLogger(_gateway.Resource).LogInformation("{Message}", message);
        return success ? CommandResults.Success(message) : CommandResults.Failure(message);
    }
}
