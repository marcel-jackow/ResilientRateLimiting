using ResilientRateLimiting.Samples.Gateway;
using Xunit;

namespace ResilientRateLimiting.Samples.Tests;

public class WeightedSequenceTests
{
    private static int[] Count(WeightedSequence sequence, int picks, int destinations)
    {
        var counts = new int[destinations];
        for (var i = 0; i < picks; i++)
        {
            counts[sequence.Next()]++;
        }

        return counts;
    }

    [Fact]
    public void Ten_picks_of_60_30_10_go_6_3_1()
    {
        Assert.Equal([6, 3, 1], Count(WeightedSequence.Parse("60/30/10", 3), 10, 3));
    }

    [Fact]
    public void A_thousand_picks_keep_the_same_shares()
    {
        Assert.Equal([600, 300, 100], Count(WeightedSequence.Parse("60/30/10", 3), 1000, 3));
    }

    [Fact]
    public void Even_is_a_plain_round_robin()
    {
        var sequence = WeightedSequence.Parse("even", 3);

        Assert.Equal([0, 1, 2, 0, 1, 2], Enumerable.Range(0, 6).Select(_ => sequence.Next()).ToArray());
    }

    [Fact]
    public void Weights_are_reduced_by_their_common_divisor()
    {
        Assert.Equal([6, 3, 1], WeightedSequence.Parse("60/30/10", 3).Weights);
    }

    [Theory]
    [InlineData("60/40")]
    [InlineData("abc")]
    [InlineData("0/50/50")]
    [InlineData("-1/2/3")]
    [InlineData("")]
    public void An_invalid_setting_names_itself_and_the_expected_count(string text)
    {
        var error = Assert.Throws<FormatException>(() => WeightedSequence.Parse(text, 3));

        Assert.Contains($"'{text}'", error.Message);
        Assert.Contains("3", error.Message);
    }
}
