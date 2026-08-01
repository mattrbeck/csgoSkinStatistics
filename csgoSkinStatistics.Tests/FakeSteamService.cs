using CSGOSkinAPI.Services;
using SteamKit2.GC.CSGO.Internal;

namespace csgoSkinStatistics.Tests;

// A SteamService with no accounts, so it never logs in and the startup connect in Program.cs has
// nothing to dial. GetItemInfoAsync answers from whatever the test set, which is the only way to
// reach the Game Coordinator leg of /api without a live Steam session.
public sealed class FakeSteamService() : SteamService(loadAccounts: false)
{
    private int _calls;

    // (s, a, d, m) -> the decoded item, or null for "the GC had nothing for this link", which is
    // also the default.
    public Func<ulong, ulong, ulong, ulong, CEconItemPreviewDataBlock?> Respond { get; set; }
        = (_, _, _, _) => null;

    public int Calls => Volatile.Read(ref _calls);

    // This service is a class-scoped fixture, so a canned answer left behind would still be
    // answering during the next test. Test classes reset it after every test.
    public void Reset()
    {
        Respond = (_, _, _, _) => null;
        Interlocked.Exchange(ref _calls, 0);
    }

    public override Task<CEconItemPreviewDataBlock?> GetItemInfoAsync(ulong s, ulong a, ulong d, ulong m)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Respond(s, a, d, m));
    }
}
