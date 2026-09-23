using Microsoft.Extensions.Logging;
using Xunit;

namespace ResilientRateLimiting.AspNetCore.Tests;

public class StoreHealthOptionsExtensionsTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public void Logs_at_warning_with_the_exception_and_its_type_name()
    {
        var logger = new RecordingLogger();
        var options = new StoreHealthOptions().WithLogging(logger);
        var exception = new InvalidOperationException("boom");

        options.OnStoreFailure!(exception);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
    }

    [Fact]
    public void Chains_a_pre_existing_callback_instead_of_replacing_it()
    {
        var seen = new List<Exception>();
        var options = new StoreHealthOptions { OnStoreFailure = seen.Add }.WithLogging(new RecordingLogger());
        var exception = new InvalidOperationException("boom");

        options.OnStoreFailure!(exception);

        Assert.Same(exception, Assert.Single(seen));
    }

    [Fact]
    public void Logs_one_warning_per_distinct_exception_type_reported_by_store_health()
    {
        var logger = new RecordingLogger();
        var options = new StoreHealthOptions().WithLogging(logger);
        var storeHealth = new StoreHealth(options);

        storeHealth.ReportFirstOccurrence(new InvalidOperationException("first"));
        storeHealth.ReportFirstOccurrence(new InvalidOperationException("same type again"));
        storeHealth.ReportFirstOccurrence(new TimeoutException("different type"));

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
    }
}
