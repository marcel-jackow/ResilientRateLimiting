namespace ResilientRateLimiting.Samples.AppHost;

/// <summary>The demo values the dashboard commands change; resources read them again on each start.</summary>
internal sealed class DemoSettings
{
    public string Weights { get; set; } = "even";

    public int ReplicaCount { get; set; } = 3;

    // The demo starts with the in-memory problem; "2.1 Switch to shared Redis" switches to the library.
    public string Mode { get; set; } = "InMemory";
}
