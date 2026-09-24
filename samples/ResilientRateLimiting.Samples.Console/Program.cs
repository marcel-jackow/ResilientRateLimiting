var useRedis = args.Contains("--redis");

if (useRedis)
{
    await Run("quick-start", Scenarios.QuickStart);
}
else
{
    Console.WriteLine("== quick-start ==");
    Console.WriteLine("Skipped: pass --redis with a Redis server on localhost:6379 to run it.");
    Console.WriteLine();
}

await Run("store-health", Scenarios.StoreHealthScenario);
await Run("local-budget", Scenarios.LocalBudgetScenario);
await Run("constructor", Scenarios.Constructor);
await Run("with-resilience", Scenarios.WithResilienceScenario);
await Run("partitioned", Scenarios.Partitioned);
await Run("partition-metrics-tag", Scenarios.PartitionMetricsTag);
await Run("fail-open", Scenarios.FailOpen);
await Run("fail-closed", Scenarios.FailClosed);
await Run("read-source", Scenarios.ReadSource);
await Run("read-retry-after", Scenarios.ReadRetryAfter);

return;

static async Task Run(string name, Func<Task> scenario)
{
    Console.WriteLine($"== {name} ==");
    await scenario();
    Console.WriteLine();
}
