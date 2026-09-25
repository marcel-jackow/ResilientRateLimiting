namespace ResilientRateLimiting.Samples.Gateway;

/// <summary>Deterministic smooth weighted round robin: over one cycle each index is picked exactly its weight times.</summary>
public sealed class WeightedSequence
{
    private readonly int[] _weights;
    private readonly int[] _current;
    private readonly int _total;
    private readonly Lock _lock = new();

    private WeightedSequence(int[] weights)
    {
        var divisor = weights.Aggregate(GreatestCommonDivisor);
        _weights = weights.Select(weight => weight / divisor).ToArray();
        _current = new int[_weights.Length];
        _total = _weights.Sum();
    }

    public IReadOnlyList<int> Weights => _weights;

    public static WeightedSequence Parse(string text, int destinationCount)
    {
        if (string.Equals(text, "even", StringComparison.OrdinalIgnoreCase))
        {
            return new WeightedSequence(Enumerable.Repeat(1, destinationCount).ToArray());
        }

        var parts = (text ?? string.Empty).Split('/');
        var weights = new int[parts.Length];
        var valid = parts.Length == destinationCount;
        for (var i = 0; valid && i < parts.Length; i++)
        {
            valid = int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out weights[i]) && weights[i] > 0;
        }

        return valid
            ? new WeightedSequence(weights)
            : throw new FormatException($"Gateway weights '{text}' must be 'even' or {destinationCount} positive whole numbers separated by '/', for example 60/30/10.");
    }

    public int Next()
    {
        lock (_lock)
        {
            var best = 0;
            for (var i = 0; i < _weights.Length; i++)
            {
                _current[i] += _weights[i];
                if (_current[i] > _current[best])
                {
                    best = i;
                }
            }

            _current[best] -= _total;
            return best;
        }
    }

    private static int GreatestCommonDivisor(int a, int b) => b == 0 ? a : GreatestCommonDivisor(b, a % b);
}
