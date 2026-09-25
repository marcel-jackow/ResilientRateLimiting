using ResilientRateLimiting.Samples.Gateway;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.LoadBalancing;

var builder = WebApplication.CreateBuilder(args);

var destinations = builder.Configuration.GetSection("Gateway:Destinations").GetChildren()
    .ToDictionary(entry => entry.Key, entry => new DestinationConfig { Address = entry.Value! });
if (destinations.Count == 0)
{
    throw new InvalidOperationException("Gateway:Destinations is empty; the AppHost sets one entry per API.");
}

var sequence = WeightedSequence.Parse(builder.Configuration["Gateway:Weights"] ?? "even", destinations.Count);
builder.Services.AddSingleton<ILoadBalancingPolicy>(new WeightedPolicy(sequence));
builder.Services.AddReverseProxy().LoadFromMemory(
    [new RouteConfig { RouteId = "all", ClusterId = "apis", Match = new RouteMatch { Path = "{**catch-all}" } }],
    [new ClusterConfig { ClusterId = "apis", LoadBalancingPolicy = "Weighted", Destinations = destinations }]);

var app = builder.Build();
app.MapReverseProxy();
app.Logger.LogInformation("Gateway weights {Weights} over {Destinations}", string.Join('/', sequence.Weights), string.Join(", ", destinations.Keys.Order(StringComparer.Ordinal)));
app.Run();
