namespace Moongazing.OrionRate.Tests;

using System;

using Xunit;

public sealed class PolicyBuilderTests
{
    [Fact]
    public void Selecting_a_second_algorithm_is_rejected_instead_of_overwriting_the_first()
    {
        Assert.Throws<InvalidOperationException>(() => new RateLimiterOptions().AddPolicy("api", p =>
            p.TokenBucket(10, TimeSpan.FromMinutes(1)).SlidingWindow(5, TimeSpan.FromMinutes(1))));

        Assert.Throws<InvalidOperationException>(() => new RateLimiterOptions().AddPolicy("api", p =>
            p.SlidingWindow(5, TimeSpan.FromMinutes(1)).TokenBucket(10, TimeSpan.FromMinutes(1))));

        Assert.Throws<InvalidOperationException>(() => new RateLimiterOptions().AddPolicy("api", p =>
            p.TokenBucket(10, TimeSpan.FromMinutes(1)).TokenBucket(20, TimeSpan.FromMinutes(1))));
    }
}
