using Microsoft.Extensions.Options;

namespace CSGOSkinAPI.Services
{
    // Tunables for SteamEgressGate, bound from the "SteamEgress" configuration section so the pace
    // can be adjusted on the box without a rebuild.
    public sealed class SteamEgressOptions
    {
        public const string SectionName = "SteamEgress";

        // Minimum spacing between two consecutive inventory fetches, whoever asked for them. Steam
        // throttles the inventory endpoint per source IP; the community-reported safe rate is
        // roughly one request every few seconds, and a ban earned by exceeding it lasts hours.
        public double MinIntervalSeconds { get; set; } = 2.0;

        // How many interactive callers may queue for the gate at once. Past this the endpoint
        // answers "busy" immediately instead of building a backlog that would still be draining
        // long after the viewers who caused it have given up.
        public int MaxWaiters { get; set; } = 24;

        // How long one interactive caller will wait in that queue before giving up.
        public double MaxWaitSeconds { get; set; } = 15.0;

        // When Steam answers 429 without a Retry-After, how long to stop asking. Every request
        // made while banned extends the ban, so this is deliberately generous.
        public double PauseOnRateLimitSeconds { get; set; } = 300.0;

        // Upper bound on any pause, including one Steam asks for via Retry-After: a bogus header
        // must not be able to take the site down for a day.
        public double MaxPauseSeconds { get; set; } = 1800.0;
    }

    public enum EgressPriority
    {
        // A viewer waiting on a page.
        Interactive,
        // The background warmer. Yields to any interactive caller and never queues.
        Background,
    }

    // A point-in-time view for /health and for the controller's "should I even try" check.
    public readonly record struct SteamEgressStatus(
        DateTimeOffset? PausedUntil,
        DateTimeOffset? LastFetchAt,
        int? LastFetchStatus,
        int Waiting);

    // One process-wide gate on every steamcommunity.com inventory fetch.
    //
    // The per-client rate limiter in Program.cs bounds what one caller can make this server send
    // to Steam. Nothing bounded the sum: a hundred first-time viewers arriving together, each for a
    // different inventory, used to produce a hundred concurrent fetches from one egress IP, which
    // is exactly the shape that earns the hours-long volume ban Steam hands out. This serializes
    // them, spaces them, and - when Steam does say 429 - stops everyone for a while rather than
    // letting the next caller renew the ban.
    //
    // Two priorities. Interactive callers queue (bounded, with a timeout) because a viewer is
    // waiting. The background warmer never queues: it takes the gate only when it is free and no
    // viewer is waiting for it, so it can never delay a page. There is no fairness concern between
    // the two beyond that - the warmer is best-effort by design.
    public sealed class SteamEgressGate(IOptions<SteamEgressOptions> options, ILogger<SteamEgressGate> logger)
    {
        private readonly SteamEgressOptions _options = options.Value;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _sync = new();

        // All guarded by _sync. Timestamps are ticks from a monotonic clock, not wall time, so a
        // clock adjustment on the host can neither shorten a pause nor strand the gate.
        private long? _lastReleaseTicks;
        private long _pausedUntilTicks = long.MinValue;
        private DateTimeOffset? _pausedUntilWall;
        private DateTimeOffset? _lastFetchAt;
        private int? _lastFetchStatus;
        private int _waiting;

        private static long Now => Environment.TickCount64;

        // Non-null while Steam has told us to stop. Callers check this before queueing so a viewer
        // gets an immediate answer (a stale copy or a clear message) instead of a timeout.
        public DateTimeOffset? PausedUntil
        {
            get
            {
                lock (_sync)
                {
                    return Now < _pausedUntilTicks ? _pausedUntilWall : null;
                }
            }
        }

        public SteamEgressStatus Status
        {
            get
            {
                lock (_sync)
                {
                    return new SteamEgressStatus(
                        Now < _pausedUntilTicks ? _pausedUntilWall : null,
                        _lastFetchAt, _lastFetchStatus, _waiting);
                }
            }
        }

        // Returns a lease to dispose after the fetch, or null when the caller should not fetch:
        // the gate is paused, the queue is full, the wait ran out, or (for Background) anyone
        // interactive is ahead. Null never means "try again immediately".
        public async Task<Lease?> TryAcquireAsync(EgressPriority priority, CancellationToken cancellationToken = default)
        {
            if (PausedUntil != null)
            {
                return null;
            }

            if (priority == EgressPriority.Background)
            {
                lock (_sync)
                {
                    if (_waiting > 0)
                    {
                        return null;
                    }
                }
                if (!await _gate.WaitAsync(TimeSpan.Zero, cancellationToken))
                {
                    return null;
                }
            }
            else
            {
                lock (_sync)
                {
                    if (_waiting >= _options.MaxWaiters)
                    {
                        logger.LogWarning("Steam egress queue full ({Waiting} waiting); refusing a fetch", _waiting);
                        return null;
                    }
                    _waiting++;
                }
                var entered = false;
                try
                {
                    entered = await _gate.WaitAsync(TimeSpan.FromSeconds(_options.MaxWaitSeconds), cancellationToken);
                }
                finally
                {
                    lock (_sync) _waiting--;
                }
                if (!entered)
                {
                    logger.LogWarning("Steam egress wait exceeded {MaxWaitSeconds}s; refusing a fetch", _options.MaxWaitSeconds);
                    return null;
                }
            }

            // Holding the gate. A pause may have begun while we queued; honour it rather than be the
            // request that extends the ban.
            if (PausedUntil != null)
            {
                _gate.Release();
                return null;
            }

            // Space this fetch from the previous one. Done while holding the gate so the spacing is
            // between fetches, not between acquisitions.
            var delay = TimeSpan.Zero;
            lock (_sync)
            {
                if (_lastReleaseTicks is long last)
                {
                    var earliest = last + (long)(_options.MinIntervalSeconds * 1000);
                    delay = TimeSpan.FromMilliseconds(Math.Max(0, earliest - Now));
                }
            }
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _gate.Release();
                    throw;
                }
            }
            return new Lease(this);
        }

        // Record what Steam said, for /health and for the pause. Called by the lease holder with
        // the upstream status; a transport failure reports null.
        public void ReportFetch(int? statusCode, TimeSpan? retryAfter = null)
        {
            lock (_sync)
            {
                _lastFetchAt = DateTimeOffset.UtcNow;
                _lastFetchStatus = statusCode;
            }
            if (statusCode == 429)
            {
                Pause(retryAfter ?? TimeSpan.FromSeconds(_options.PauseOnRateLimitSeconds), retryAfter != null);
            }
        }

        private void Pause(TimeSpan duration, bool fromRetryAfter)
        {
            var cap = TimeSpan.FromSeconds(_options.MaxPauseSeconds);
            if (duration > cap)
            {
                duration = cap;
            }
            if (duration <= TimeSpan.Zero)
            {
                return;
            }
            lock (_sync)
            {
                var until = Now + (long)duration.TotalMilliseconds;
                // Never shorten a pause already in force.
                if (until > _pausedUntilTicks)
                {
                    _pausedUntilTicks = until;
                    _pausedUntilWall = DateTimeOffset.UtcNow + duration;
                }
            }
            logger.LogWarning(
                "Steam inventory fetches paused for {PauseSeconds:0}s after a 429 ({Source})",
                duration.TotalSeconds, fromRetryAfter ? "Retry-After" : "default pause");
        }

        private void Release()
        {
            lock (_sync)
            {
                _lastReleaseTicks = Now;
            }
            _gate.Release();
        }

        public sealed class Lease : IDisposable
        {
            private SteamEgressGate? _owner;

            internal Lease(SteamEgressGate owner) => _owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release();
            }
        }
    }
}
