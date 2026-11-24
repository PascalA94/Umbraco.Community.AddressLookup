using Microsoft.Extensions.Time.Testing;
using Umbraco.Community.AddressLookup.Configuration;
using Umbraco.Community.AddressLookup.Security;
using Umbraco.Community.AddressLookup.Tests.Support;

namespace Umbraco.Community.AddressLookup.Tests.Security;

public class RateLimiterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 16, 23, 0, 0, TimeSpan.Zero));

    private LookupRateLimiter Create(Action<RateLimitOptions>? tweak = null)
    {
        var options = new AddressLookupOptions();
        tweak?.Invoke(options.RateLimits);
        return new LookupRateLimiter(new TestOptionsMonitor<AddressLookupOptions>(options), _time);
    }

    [Fact]
    public void Find_31stInMinute_Denied()
    {
        using var limiter = Create();

        for (var i = 0; i < 30; i++)
        {
            Assert.True(limiter.TryAcquire(LookupKind.Find, "p1", "10.0.0.1").Allowed, $"find {i + 1}");
        }

        var denied = limiter.TryAcquire(LookupKind.Find, "p1", "10.0.0.1");
        Assert.False(denied.Allowed);
        Assert.False(denied.DailyCapHit);
        Assert.NotNull(denied.RetryAfter);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
        Assert.True(limiter.TryAcquire(LookupKind.Find, "p2", "10.0.0.2").Allowed);
    }

    [Fact]
    public void Retrieve_7thInMinute_Denied()
    {
        using var limiter = Create();

        for (var i = 0; i < 6; i++)
        {
            Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "p1", "10.0.0.1").Allowed);
        }

        Assert.False(limiter.TryAcquire(LookupKind.Retrieve, "p1", "10.0.0.1").Allowed);
    }

    [Fact]
    public void FindAndRetrieve_HaveSeparateBudgets()
    {
        using var limiter = Create();
        for (var i = 0; i < 6; i++)
        {
            limiter.TryAcquire(LookupKind.Retrieve, "p1", "10.0.0.1");
        }

        Assert.True(limiter.TryAcquire(LookupKind.Find, "p1", "10.0.0.1").Allowed);
    }

    [Fact]
    public void RetrievePerDay_GlobalCap()
    {
        using var limiter = Create(o => { o.RetrievePerDay = 1000; o.RetrievePerMinute = 1000; });

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(limiter.TryAcquire(LookupKind.Retrieve, $"ticket-{i}", $"10.1.{i / 250}.{i % 250}").Allowed, $"retrieve {i + 1}");
        }

        var denied = limiter.TryAcquire(LookupKind.Retrieve, "ticket-1000", "10.9.9.9");
        Assert.False(denied.Allowed);
        Assert.True(denied.DailyCapHit);
        Assert.True(limiter.TryAcquire(LookupKind.Find, "p1", "10.0.0.1").Allowed);
    }

    [Fact]
    public void DailyCapHit_IsFlaggedForLoggingOncePerDay()
    {
        using var limiter = Create(o => { o.RetrievePerDay = 1; o.RetrievePerMinute = 100; });
        limiter.TryAcquire(LookupKind.Retrieve, "a", "10.2.0.1");

        var first = limiter.TryAcquire(LookupKind.Retrieve, "b", "10.2.0.2");
        var second = limiter.TryAcquire(LookupKind.Retrieve, "c", "10.2.0.3");

        Assert.True(first.DailyCapHit && first.FirstDailyCapHit);
        Assert.True(second.DailyCapHit && !second.FirstDailyCapHit);
    }

    [Fact]
    public void DailyCap_ResetsAtUtcMidnight()
    {
        using var limiter = Create(o => { o.RetrievePerDay = 1; o.RetrievePerMinute = 100; });
        Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "a", "10.2.0.1").Allowed);
        Assert.False(limiter.TryAcquire(LookupKind.Retrieve, "b", "10.2.0.2").Allowed);

        _time.Advance(TimeSpan.FromHours(1)); // 00:00 UTC next day

        Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "c", "10.2.0.3").Allowed);
        Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "d", "10.2.0.4").DailyCapHit);
        Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "e", "10.2.0.5").DailyCapHit);
    }

    [Fact]
    public void MinuteDenial_DoesNotSpendTheDailyBudget()
    {
        using var limiter = Create(o => { o.RetrievePerDay = 7; o.RetrievePerMinute = 6; });
        for (var i = 0; i < 6; i++)
        {
            limiter.TryAcquire(LookupKind.Retrieve, "p1", "10.0.0.1");
        }

        for (var i = 0; i < 20; i++)
        {
            Assert.False(limiter.TryAcquire(LookupKind.Retrieve, "p1", "10.0.0.1").Allowed);
        }

        Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "p2", "10.0.0.2").Allowed);
    }

    [Fact]
    public void RotatingTickets_FromOneIp_StillHitThePerIpLimit()
    {
        using var limiter = Create(o => { o.FindPerMinute = 30; o.FindPerMinutePerIp = 60; });

        for (var i = 0; i < 60; i++)
        {
            Assert.True(limiter.TryAcquire(LookupKind.Find, $"fresh-ticket-{i}", "203.0.113.7").Allowed, $"find {i + 1}");
        }

        var denied = limiter.TryAcquire(LookupKind.Find, "fresh-ticket-61", "203.0.113.7");
        Assert.False(denied.Allowed);
        Assert.False(denied.DailyCapHit);
        Assert.True(limiter.TryAcquire(LookupKind.Find, "fresh-ticket-61", "203.0.113.8").Allowed);
    }

    [Fact]
    public void RotatingTickets_FromOneIp_CannotDrainRetrieve()
    {
        using var limiter = Create(o => { o.RetrievePerMinute = 6; o.RetrievePerMinutePerIp = 12; o.RetrievePerDay = 1000; });

        var allowed = Enumerable.Range(0, 40).Count(i => limiter.TryAcquire(LookupKind.Retrieve, $"t{i}", "203.0.113.7").Allowed);

        Assert.Equal(12, allowed);
    }

    [Fact]
    public void FindPerDay_GlobalCap_FlagsOneWarningPerDay()
    {
        using var limiter = Create(o => { o.FindPerDay = 2; o.FindPerMinute = 100; o.FindPerMinutePerIp = 100; });
        limiter.TryAcquire(LookupKind.Find, "a", "10.3.0.1");
        limiter.TryAcquire(LookupKind.Find, "b", "10.3.0.2");

        var first = limiter.TryAcquire(LookupKind.Find, "c", "10.3.0.3");
        var second = limiter.TryAcquire(LookupKind.Find, "d", "10.3.0.4");

        Assert.True(first.DailyCapHit && first.FirstDailyCapHit && !first.Allowed);
        Assert.True(second.DailyCapHit && !second.FirstDailyCapHit);
        Assert.True(limiter.TryAcquire(LookupKind.Retrieve, "e", "10.3.0.5").Allowed);
    }

    [Fact]
    public void PartitionKeys_AreKeyedHashes_NotRawValues()
    {
        using var limiter = Create();
        using var other = Create();
        const string ip = "203.0.113.77";

        var key = limiter.KeyFor(ip);

        Assert.DoesNotContain(ip, key);
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(key, limiter.KeyFor(ip));
        Assert.NotEqual(key, limiter.KeyFor("203.0.113.78"));
        // A per-instance random key: the small IPv4 space cannot be brute-forced from a dumped key.
        Assert.NotEqual(key, other.KeyFor(ip));
        Assert.Matches("^[0-9a-f]{64}$", limiter.KeyFor(null));
    }
}
