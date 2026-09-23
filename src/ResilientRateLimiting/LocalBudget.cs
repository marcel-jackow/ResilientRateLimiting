namespace ResilientRateLimiting;

/// <summary>Sizes the in-memory fallback limiter a caller supplies. Arithmetic only: the library never builds that limiter, so it cannot apply this itself.</summary>
public static class LocalBudget
{
    /// <summary>The share of <paramref name="sharedPermitLimit"/> one replica may spend while the store cannot answer, rounded up.</summary>
    /// <param name="sharedPermitLimit">The limit enforced across every replica together.</param>
    /// <param name="replicaCount">Replicas normally running. Use the count you typically run, not the maximum.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either argument is below one.</exception>
    public static int ForReplicas(int sharedPermitLimit, int replicaCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sharedPermitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(replicaCount, 1);

        return (int)Math.Ceiling(sharedPermitLimit / (double)replicaCount);
    }
}
