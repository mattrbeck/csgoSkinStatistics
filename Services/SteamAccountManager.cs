namespace CSGOSkinAPI.Services
{
    public class SteamAccountManager
    {
        public SteamClient Client { get; }
        public SteamUser User { get; }
        public SteamGameCoordinator GC { get; }
        public CallbackManager Manager { get; }
        public SteamAccount Account { get; }
        public bool IsConnected { get; set; }
        public bool IsLoggedIn { get; set; }
        // Whether the in-flight logon used a cached refresh token, so a rejection can fall back
        // to a fresh credential auth (see SteamService.OnLoggedOn).
        public bool UsedCachedToken { get; set; }
        public DateTime LastRequestTime { get; set; } = DateTime.MinValue;
        public SemaphoreSlim RateLimitSemaphore { get; } = new(1, 1);

        public SteamAccountManager(SteamAccount account)
        {
            Account = account;
            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>()!;
            GC = Client.GetHandler<SteamGameCoordinator>()!;
            Manager = new CallbackManager(Client);
        }

        public void Dispose()
        {
            RateLimitSemaphore?.Dispose();
            Client?.Disconnect();
        }
    }
}
