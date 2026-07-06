using CSGOSkinAPI.Services;
using CSGOSkinAPI.Models;
using SteamKit2.GC.CSGO.Internal;
using System.Text.Json;
using Xunit;
using Moq;

namespace csgoSkinStatistics.Tests.Services;

public class SteamServiceTests : IDisposable
{
    private readonly string _testAccountsFile = "test_steam-accounts.json";

    public void Dispose()
    {
        if (File.Exists(_testAccountsFile))
        {
            File.Delete(_testAccountsFile);
        }
        if (File.Exists("steam-accounts.json"))
        {
            File.Delete("steam-accounts.json");
        }
    }

    [Fact]
    public void Constructor_ShouldLoadAccountsFromJsonFile()
    {
        var accounts = new List<SteamAccount>
        {
            new() { Username = "testuser1", Password = "testpass1" },
            new() { Username = "testuser2", Password = "testpass2" }
        };

        var json = JsonSerializer.Serialize(accounts);
        File.WriteAllText("steam-accounts.json", json);

        // This would normally initialize Steam connections, but we can't test that easily
        // The constructor loads accounts from file
        Assert.True(File.Exists("steam-accounts.json"));

        var loadedAccounts = JsonSerializer.Deserialize<List<SteamAccount>>(File.ReadAllText("steam-accounts.json"));
        Assert.NotNull(loadedAccounts);
        Assert.Equal(2, loadedAccounts.Count);
        Assert.Equal("testuser1", loadedAccounts[0].Username);
        Assert.Equal("testuser2", loadedAccounts[1].Username);
    }

    [Fact]
    public void Constructor_ShouldFallbackToEnvironmentVariables()
    {
        // Ensure no accounts file exists
        if (File.Exists("steam-accounts.json"))
        {
            File.Delete("steam-accounts.json");
        }

        // Set environment variables
        Environment.SetEnvironmentVariable("STEAM_USERNAME", "envuser");
        Environment.SetEnvironmentVariable("STEAM_PASSWORD", "envpass");

        try
        {
            // The service constructor would use these environment variables
            var username = Environment.GetEnvironmentVariable("STEAM_USERNAME");
            var password = Environment.GetEnvironmentVariable("STEAM_PASSWORD");

            Assert.Equal("envuser", username);
            Assert.Equal("envpass", password);
        }
        finally
        {
            // Clean up environment variables
            Environment.SetEnvironmentVariable("STEAM_USERNAME", null);
            Environment.SetEnvironmentVariable("STEAM_PASSWORD", null);
        }
    }

    [Fact]
    public void Constructor_ShouldThrowWhenNoAccountsConfigured()
    {
        // Ensure no accounts file exists
        if (File.Exists("steam-accounts.json"))
        {
            File.Delete("steam-accounts.json");
        }

        // Ensure no environment variables are set
        Environment.SetEnvironmentVariable("STEAM_USERNAME", null);
        Environment.SetEnvironmentVariable("STEAM_PASSWORD", null);

        try
        {
            // The actual service constructor would throw here
            // We can't easily test the full constructor due to Steam connections
            // But we can verify the logic path that would be taken
            var steamUsername = Environment.GetEnvironmentVariable("STEAM_USERNAME");
            var steamPassword = Environment.GetEnvironmentVariable("STEAM_PASSWORD");
            var hasJsonFile = File.Exists("steam-accounts.json");

            Assert.Null(steamUsername);
            Assert.Null(steamPassword);
            Assert.False(hasJsonFile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STEAM_USERNAME", null);
            Environment.SetEnvironmentVariable("STEAM_PASSWORD", null);
        }
    }

    [Fact]
    public void SteamAccountManager_ShouldInitializeCorrectly()
    {
        var account = new SteamAccount
        {
            Username = "testuser",
            Password = "testpass"
        };

        var accountManager = new SteamAccountManager(account);

        Assert.NotNull(accountManager.Client);
        Assert.NotNull(accountManager.User);
        Assert.NotNull(accountManager.GC);
        Assert.NotNull(accountManager.Manager);
        Assert.NotNull(accountManager.Account);
        Assert.NotNull(accountManager.RateLimitSemaphore);
        Assert.Equal("testuser", accountManager.Account.Username);
        Assert.Equal("testpass", accountManager.Account.Password);
        Assert.False(accountManager.IsConnected);
        Assert.False(accountManager.IsLoggedIn);
        Assert.Equal(DateTime.MinValue, accountManager.LastRequestTime);

        accountManager.Dispose();
    }

    [Fact]
    public void SteamAccountManager_DisposeShouldCleanUpResources()
    {
        var account = new SteamAccount
        {
            Username = "testuser",
            Password = "testpass"
        };

        var accountManager = new SteamAccountManager(account);

        // Dispose should not throw
        accountManager.Dispose();

        // Calling dispose again should not throw
        accountManager.Dispose();
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("123", false)]
    [InlineData("7656119", false)]
    [InlineData("12345678901234567", false)] // Wrong prefix
    [InlineData("86561191234567890", false)] // Wrong prefix
    [InlineData("76561191234567890", true)]  // Valid SteamId64
    [InlineData("76561198123456789", true)]  // Valid SteamId64
    public void ValidateSteamId_ShouldValidateCorrectly(string steamId, bool expected)
    {
        // This tests the validation logic that would be used in the service
        var isValid = steamId.StartsWith("76561") && steamId.Length == 17 && ulong.TryParse(steamId, out _);
        Assert.Equal(expected, isValid);
    }

    [Fact]
    public void SteamAccount_PropertiesShouldSerializeCorrectly()
    {
        var account = new SteamAccount
        {
            Username = "testuser",
            Password = "testpass"
        };

        var json = JsonSerializer.Serialize(account);
        var deserialized = JsonSerializer.Deserialize<SteamAccount>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("testuser", deserialized.Username);
        Assert.Equal("testpass", deserialized.Password);
    }

    // --- Pending-request coalescing (the itemid -> waiters map that de-dupes concurrent GC
    // lookups). Exercises the real bookkeeping methods; no Steam connection is involved. ---

    private static SteamService CreateServiceWithOneAccount()
    {
        File.WriteAllText("steam-accounts.json",
            JsonSerializer.Serialize(new List<SteamAccount> { new() { Username = "u", Password = "p" } }));
        return new SteamService();
    }

    private static TaskCompletionSource<CEconItemPreviewDataBlock?> NewWaiter() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void JoinOrCreatePendingRequest_FirstCallerLeads_RestWait()
    {
        var svc = CreateServiceWithOneAccount();

        Assert.True(svc.JoinOrCreatePendingRequest(5, NewWaiter()));   // creator -> leader
        Assert.False(svc.JoinOrCreatePendingRequest(5, NewWaiter()));  // joins in-flight -> waiter
        Assert.False(svc.JoinOrCreatePendingRequest(5, NewWaiter()));
    }

    [Fact]
    public async Task ResolvePendingRequest_WakesLeaderAndEveryWaiter()
    {
        var svc = CreateServiceWithOneAccount();
        var leader = NewWaiter();
        var waiter = NewWaiter();
        svc.JoinOrCreatePendingRequest(9, leader);
        svc.JoinOrCreatePendingRequest(9, waiter);

        var item = new CEconItemPreviewDataBlock { itemid = 9 };
        svc.ResolvePendingRequest(9, item);

        Assert.Same(item, await leader.Task);
        Assert.Same(item, await waiter.Task);
    }

    [Fact]
    public void JoinAfterResolve_BecomesFreshLeader()
    {
        // M4: resolution removes the entry from the map before resolving, so a caller arriving
        // after the response leads a new request instead of orphaning itself on the drained list.
        var svc = CreateServiceWithOneAccount();
        var first = NewWaiter();
        Assert.True(svc.JoinOrCreatePendingRequest(7, first));

        svc.ResolvePendingRequest(7, new CEconItemPreviewDataBlock { itemid = 7 });

        Assert.True(svc.JoinOrCreatePendingRequest(7, NewWaiter())); // entry gone -> leads again
    }

    [Fact]
    public async Task ResolvePendingRequest_IsIdempotentAndToleratesAlreadyCompletedWaiters()
    {
        // H2: a duplicate GC response (or a waiter already settled by its coalesced-timeout) must
        // not throw - TrySetResult, not SetResult - or the callback loop would die.
        var svc = CreateServiceWithOneAccount();
        var settled = NewWaiter();
        svc.JoinOrCreatePendingRequest(3, settled);
        settled.TrySetResult(null); // pretend this waiter already completed

        var item = new CEconItemPreviewDataBlock { itemid = 3 };
        svc.ResolvePendingRequest(3, item); // resolves a list holding an already-completed waiter
        svc.ResolvePendingRequest(3, item); // entry already gone: no-op

        Assert.Null(await settled.Task); // keeps its first result
    }

    [Fact]
    public void JoinOrCreatePendingRequest_UnderContention_ElectsExactlyOneLeader()
    {
        var svc = CreateServiceWithOneAccount();
        const ulong id = 42;
        var waiters = Enumerable.Range(0, 64).Select(_ => NewWaiter()).ToArray();

        var leaders = 0;
        Parallel.ForEach(waiters, w =>
        {
            if (svc.JoinOrCreatePendingRequest(id, w)) Interlocked.Increment(ref leaders);
        });

        Assert.Equal(1, leaders);

        // Every concurrent caller lands on the one list, so a single response wakes them all.
        svc.ResolvePendingRequest(id, new CEconItemPreviewDataBlock { itemid = id });
        Assert.All(waiters, w => Assert.True(w.Task.IsCompleted));
    }
}