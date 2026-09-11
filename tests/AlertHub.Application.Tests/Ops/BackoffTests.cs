using AlertHub.Application.Ops;

namespace AlertHub.Application.Tests.Ops;

[Trait("Category", "Unit")]
public sealed class BackoffTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(10, 2560)]
    [InlineData(11, 3600)]
    [InlineData(50, 3600)]
    public void Delay_is_at_least_one_second_and_never_above_the_exponential_cap(int attempt, int capSeconds)
    {
        var random = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var delay = Backoff.For(attempt, random);
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(capSeconds));
        }
    }

    [Fact]
    public void Jitter_spreads_retries()
    {
        var delays = Enumerable.Range(0, 100).Select(_ => Backoff.For(5)).Distinct().Count();
        delays.Should().BeGreaterThan(50);
    }
}
