using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Umbraco.Community.AddressLookup.Configuration;

namespace Umbraco.Community.AddressLookup.Security;

public enum LookupKind
{
    Find,
    Retrieve,
}

/// <param name="Allowed">Whether the call may go to the provider.</param>
/// <param name="RetryAfter">How long a throttled caller should wait.</param>
/// <param name="DailyCapHit">A global daily cap is spent, so every call of that kind is refused until UTC midnight.</param>
/// <param name="FirstDailyCapHit">True for the first refusal of the day, so the caller logs one Warning a day.</param>
public readonly record struct RateLimitDecision(bool Allowed, TimeSpan? RetryAfter, bool DailyCapHit, bool FirstDailyCapHit = false);

/// <summary>
/// In-process throttle (design 5.2). Four sliding-window budgets (per ticket and per client IP, for Find and Retrieve)
/// plus a global daily cap for each. The per-IP budget exists because a ticket costs one page view, so a per-ticket limit
/// alone is reset by fetching a new ticket. Limits are per instance; load-balanced sites also set the provider's own key
/// limit.
/// </summary>
public sealed class LookupRateLimiter : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _findByTicket;
    private readonly PartitionedRateLimiter<string> _findByIp;
    private readonly PartitionedRateLimiter<string> _retrieveByTicket;
    private readonly PartitionedRateLimiter<string> _retrieveByIp;
    private readonly DailyCap _findDaily;
    private readonly DailyCap _retrieveDaily;
    private readonly TimeProvider _time;

    // A random per-instance key: state never holds a raw ticket or IP, and a dumped key cannot be reversed by trying the
    // small IPv4 space (a bare hash could be). The key is never persisted.
    private readonly byte[] _hashKey = RandomNumberGenerator.GetBytes(32);

    public LookupRateLimiter(IOptionsMonitor<AddressLookupOptions> options, TimeProvider time)
    {
        var limits = options.CurrentValue.RateLimits;
        _time = time;
        _findByTicket = Create(limits.FindPerMinute);
        _findByIp = Create(limits.FindPerMinutePerIp);
        _retrieveByTicket = Create(limits.RetrievePerMinute);
        _retrieveByIp = Create(limits.RetrievePerMinutePerIp);
        _findDaily = new DailyCap(limits.FindPerDay, time);
        _retrieveDaily = new DailyCap(limits.RetrievePerDay, time);
    }

    /// <summary>A keyed SHA-256 of a ticket or IP, as lower-case hex. A null value hashes as empty.</summary>
    internal string KeyFor(string? value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(value ?? string.Empty)));

    public RateLimitDecision TryAcquire(LookupKind kind, string ticket, string? clientIp)
    {
        var daily = kind == LookupKind.Find ? _findDaily : _retrieveDaily;
        if (daily.Spent() is { } capped)
        {
            return capped;
        }

        var byTicket = kind == LookupKind.Find ? _findByTicket : _retrieveByTicket;
        var byIp = kind == LookupKind.Find ? _findByIp : _retrieveByIp;
        foreach (var (limiter, key) in new[] { (byTicket, KeyFor(ticket)), (byIp, KeyFor(clientIp)) })
        {
            using var lease = limiter.AttemptAcquire(key);
            if (!lease.IsAcquired)
            {
                var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : Window;
                return new RateLimitDecision(false, retry, false);
            }
        }

        // Counted only after the per-minute budgets pass, so throttled callers do not spend the day's budget.
        return daily.TrySpend() ?? new RateLimitDecision(true, null, false);
    }

    public void Dispose()
    {
        _findByTicket.Dispose();
        _findByIp.Dispose();
        _retrieveByTicket.Dispose();
        _retrieveByIp.Dispose();
    }

    private static PartitionedRateLimiter<string> Create(int permitsPerMinute) =>
        PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitsPerMinute,
                Window = Window,
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    private sealed class DailyCap(int limit, TimeProvider time)
    {
        private readonly Lock _gate = new();
        private DateOnly _day = Today(time);
        private int _count;
        private bool _logged;

        public RateLimitDecision? Spent()
        {
            lock (_gate)
            {
                Roll();
                return _count >= limit ? Denied() : null;
            }
        }

        /// <summary>Counts one call; returns a denial when a concurrent call took the last permit.</summary>
        public RateLimitDecision? TrySpend()
        {
            lock (_gate)
            {
                Roll();
                if (_count >= limit)
                {
                    return Denied();
                }

                _count++;
                return null;
            }
        }

        private RateLimitDecision Denied()
        {
            var first = !_logged;
            _logged = true;
            var untilMidnight = _day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - time.GetUtcNow().UtcDateTime;
            return new RateLimitDecision(false, untilMidnight > TimeSpan.Zero ? untilMidnight : null, true, first);
        }

        private void Roll()
        {
            var today = Today(time);
            if (today != _day)
            {
                _day = today;
                _count = 0;
                _logged = false;
            }
        }

        private static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    }
}
