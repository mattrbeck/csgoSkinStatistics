using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSGOSkinAPI.Services;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// The robust login flow can't be exercised without a live Steam handshake, so the part that is
// testable - the refresh-token cache it depends on - is covered here in isolation.
public class SteamTokenStoreTests : IDisposable
{
    private readonly string _path = $"test_tokens_{Guid.NewGuid():N}.json";

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Get_ReturnsTokenAfterSet()
    {
        var store = new SteamTokenStore(_path);
        store.Set("alice", "refresh-abc");
        Assert.Equal("refresh-abc", store.Get("alice"));
    }

    [Fact]
    public void Get_UnknownUsername_ReturnsNull()
    {
        var store = new SteamTokenStore(_path);
        store.Set("alice", "refresh-abc");
        Assert.Null(store.Get("bob"));
    }

    [Fact]
    public void Get_MissingFile_ReturnsNullWithoutThrowing()
    {
        var store = new SteamTokenStore(_path);
        Assert.False(File.Exists(_path));
        Assert.Null(store.Get("alice")); // must not create the file or throw
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Set_Overwrites_KeepsLatestToken()
    {
        var store = new SteamTokenStore(_path);
        store.Set("alice", "old");
        store.Set("alice", "new");
        Assert.Equal("new", store.Get("alice"));
    }

    [Fact]
    public void Set_MultipleUsers_AreIsolated()
    {
        var store = new SteamTokenStore(_path);
        store.Set("alice", "a-token");
        store.Set("bob", "b-token");
        Assert.Equal("a-token", store.Get("alice"));
        Assert.Equal("b-token", store.Get("bob"));
    }

    [Fact]
    public void Remove_DropsTokenButKeepsOthers()
    {
        var store = new SteamTokenStore(_path);
        store.Set("alice", "a-token");
        store.Set("bob", "b-token");

        store.Remove("alice");

        Assert.Null(store.Get("alice"));
        Assert.Equal("b-token", store.Get("bob"));
    }

    [Fact]
    public void Remove_UnknownUsername_DoesNotThrow()
    {
        var store = new SteamTokenStore(_path);
        store.Remove("nobody"); // no file yet
        store.Set("alice", "a-token");
        store.Remove("nobody"); // present file, absent key
        Assert.Equal("a-token", store.Get("alice"));
    }

    [Fact]
    public void Persists_AcrossInstances_OnSamePath()
    {
        new SteamTokenStore(_path).Set("alice", "refresh-abc");

        // A fresh process/instance must read what the previous one wrote.
        var reopened = new SteamTokenStore(_path);
        Assert.Equal("refresh-abc", reopened.Get("alice"));
    }

    [Fact]
    public void CorruptFile_ReadsAsEmpty_AndRecoversOnNextSet()
    {
        File.WriteAllText(_path, "{ this is not valid json");
        var store = new SteamTokenStore(_path);

        Assert.Null(store.Get("alice")); // tolerates the garbage instead of throwing

        store.Set("alice", "refresh-abc"); // overwrites the corrupt file
        Assert.Equal("refresh-abc", store.Get("alice"));
        Assert.Null(store.Get("bob"));
    }

    [Fact]
    public async Task ConcurrentWrites_FromManyThreads_AllReadBack()
    {
        var store = new SteamTokenStore(_path);
        // Each account logs on from its own thread, so concurrent Set must not corrupt the file.
        var users = Enumerable.Range(0, 50).Select(i => $"user{i}").ToList();

        await Task.WhenAll(users.Select(u => Task.Run(() => store.Set(u, $"token-{u}"))));

        foreach (var u in users)
        {
            Assert.Equal($"token-{u}", store.Get(u));
        }
    }
}
