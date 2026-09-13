using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSGOSkinAPI.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

public class SteamEgressGateTests
{
    private static SteamEgressGate Build(Action<SteamEgressOptions>? configure = null)
    {
        var options = new SteamEgressOptions { MinIntervalSeconds = 0, MaxWaitSeconds = 2, MaxWaiters = 2 };
        configure?.Invoke(options);
        return new SteamEgressGate(Options.Create(options), NullLogger<SteamEgressGate>.Instance);
    }

    [Fact]
    public async Task OneLeaseAtATime_AndTheNextWaitsForItsRelease()
    {
        var gate = Build();
        using var first = await gate.TryAcquireAsync(EgressPriority.Interactive);
        Assert.NotNull(first);

        var second = gate.TryAcquireAsync(EgressPriority.Interactive);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var lease = await second;
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task ConsecutiveFetches_AreSpacedByTheMinimumInterval()
    {
        var gate = Build(o => o.MinIntervalSeconds = 0.3);
        (await gate.TryAcquireAsync(EgressPriority.Interactive))!.Dispose();

        var clock = Stopwatch.StartNew();
        using var second = await gate.TryAcquireAsync(EgressPriority.Interactive);
        Assert.NotNull(second);
        Assert.True(clock.ElapsedMilliseconds >= 250, $"second lease came after {clock.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task QueueFull_RefusesImmediatelyRatherThanWaiting()
    {
        var gate = Build(o => o.MaxWaiters = 1);
        using var holder = await gate.TryAcquireAsync(EgressPriority.Interactive);
        var queued = gate.TryAcquireAsync(EgressPriority.Interactive);
        await Task.Delay(50);

        var clock = Stopwatch.StartNew();
        var refused = await gate.TryAcquireAsync(EgressPriority.Interactive);

        Assert.Null(refused);
        Assert.True(clock.ElapsedMilliseconds < 500, "a full queue must answer at once");
        holder!.Dispose();
        (await queued)?.Dispose();
    }

    [Fact]
    public async Task WaitExceeded_ReturnsNullAndLeavesTheGateUsable()
    {
        var gate = Build(o => o.MaxWaitSeconds = 0.2);
        var holder = await gate.TryAcquireAsync(EgressPriority.Interactive);

        var timedOut = await gate.TryAcquireAsync(EgressPriority.Interactive);
        Assert.Null(timedOut);

        holder!.Dispose();
        using var after = await gate.TryAcquireAsync(EgressPriority.Interactive);
        Assert.NotNull(after);
        Assert.Equal(0, gate.Status.Waiting);
    }

    [Fact]
    public async Task A429_PausesEveryCallerUntilRetryAfterElapses()
    {
        var gate = Build();
        gate.ReportFetch(429, TimeSpan.FromMilliseconds(400));

        Assert.NotNull(gate.PausedUntil);
        Assert.Null(await gate.TryAcquireAsync(EgressPriority.Interactive));
        Assert.Null(await gate.TryAcquireAsync(EgressPriority.Background));

        await Task.Delay(500);
        Assert.Null(gate.PausedUntil);
        using var lease = await gate.TryAcquireAsync(EgressPriority.Interactive);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task A429WithoutRetryAfter_UsesTheDefaultPause_CappedByMaxPause()
    {
        var gate = Build(o =>
        {
            o.PauseOnRateLimitSeconds = 3600;
            o.MaxPauseSeconds = 0.3;
        });
        gate.ReportFetch(429);

        var pausedUntil = gate.PausedUntil;
        Assert.NotNull(pausedUntil);
        Assert.True(pausedUntil <= DateTimeOffset.UtcNow.AddSeconds(0.5), "the cap must bound the default pause");
        await Task.Delay(400);
        Assert.Null(gate.PausedUntil);
    }

    [Fact]
    public void MaxPauseOfZero_DisablesPausingEntirely()
    {
        var gate = Build(o => o.MaxPauseSeconds = 0);
        gate.ReportFetch(429, TimeSpan.FromHours(1));
        Assert.Null(gate.PausedUntil);
    }

    [Fact]
    public async Task ALongerPause_IsNeverShortenedByALaterShorterOne()
    {
        var gate = Build();
        gate.ReportFetch(429, TimeSpan.FromSeconds(30));
        var longPause = gate.PausedUntil;
        gate.ReportFetch(429, TimeSpan.FromMilliseconds(10));
        Assert.Equal(longPause, gate.PausedUntil);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Background_NeverQueues_AndYieldsToAWaitingInteractiveCaller()
    {
        var gate = Build();
        var holder = await gate.TryAcquireAsync(EgressPriority.Interactive);

        // Gate busy: background is refused at once instead of queueing.
        var clock = Stopwatch.StartNew();
        Assert.Null(await gate.TryAcquireAsync(EgressPriority.Background));
        Assert.True(clock.ElapsedMilliseconds < 500);

        // An interactive caller is queued: background is refused even though it could not have
        // entered anyway, and stays refused the instant the gate frees, because the interactive
        // waiter is ahead.
        var queued = gate.TryAcquireAsync(EgressPriority.Interactive);
        await Task.Delay(50);
        Assert.Equal(1, gate.Status.Waiting);
        Assert.Null(await gate.TryAcquireAsync(EgressPriority.Background));

        holder!.Dispose();
        using var interactive = await queued;
        Assert.NotNull(interactive);
    }

    [Fact]
    public async Task Background_TakesAFreeGate()
    {
        var gate = Build();
        using var lease = await gate.TryAcquireAsync(EgressPriority.Background);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task ReportFetch_IsVisibleInStatus_And403IsNotAPause()
    {
        var gate = Build();
        gate.ReportFetch(403);
        var status = gate.Status;
        Assert.Equal(403, status.LastFetchStatus);
        Assert.NotNull(status.LastFetchAt);
        Assert.Null(status.PausedUntil);

        gate.ReportFetch(null);
        Assert.Null(gate.Status.LastFetchStatus);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposingALeaseTwice_ReleasesOnce()
    {
        var gate = Build();
        var lease = await gate.TryAcquireAsync(EgressPriority.Interactive);
        lease!.Dispose();
        lease.Dispose();

        // If the double dispose had released twice, two callers could hold the gate together.
        using var a = await gate.TryAcquireAsync(EgressPriority.Interactive);
        var b = gate.TryAcquireAsync(EgressPriority.Background);
        Assert.Null(await b);
        Assert.NotNull(a);
    }

    [Fact]
    public async Task ManyConcurrentInteractiveCallers_AreServedOneAtATime()
    {
        var gate = Build(o => { o.MaxWaiters = 50; o.MaxWaitSeconds = 10; });
        var concurrent = 0;
        var peak = 0;
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            using var lease = await gate.TryAcquireAsync(EgressPriority.Interactive);
            Assert.NotNull(lease);
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(5);
            Interlocked.Decrement(ref concurrent);
        });
        await Task.WhenAll(tasks);
        Assert.Equal(1, peak);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
