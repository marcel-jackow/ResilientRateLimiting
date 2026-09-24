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

await Run("built-in-limiter", Scenarios.BuiltInLimiter);
await Run("limiter-kinds", Scenarios.LimiterKinds);
await Run("built-in-partitioned", Scenarios.BuiltInPartitioned);
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
await Run("store-failure-callback", Scenarios.StoreFailureCallback);
await Run("options-copy", Scenarios.OptionsCopy);
await Run("validate", Scenarios.Validate);
await Run("time-provider", Scenarios.TimeProviderScenario);
await Run("acquire-async-not-attempt", Scenarios.AcquireAsyncNotAttempt);
await Run("dispose", Scenarios.DisposeScenario);
await Run("permit-count-check", Scenarios.PermitCountCheck);
await Run("token-bucket-fallback", Scenarios.TokenBucketFallback);
await Run("sliding-window-fallback", Scenarios.SlidingWindowFallback);

return;

static async Task Run(string name, Func<Task> scenario)
{
    Console.WriteLine($"== {name} ==");
    await scenario();
    Console.WriteLine();
}
