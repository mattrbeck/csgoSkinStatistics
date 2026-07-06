namespace CSGOSkinAPI.Services
{
    public class SteamService
    {
        private readonly List<SteamAccountManager> _accountManagers = [];
        private readonly SteamTokenStore _tokenStore = new("steam-tokens.json");
        // Written on the HTTP thread (ConnectAsync/Disconnect), read on the per-account callback
        // loop threads, so it must be volatile for those loops to observe a shutdown promptly.
        private volatile bool _isRunning = false;
        private readonly ConcurrentDictionary<ulong, List<TaskCompletionSource<CEconItemPreviewDataBlock?>>> _pendingRequests = new();
        private int _currentAccountIndex = 0;
        private readonly object _accountSelectionLock = new();


        public SteamService()
        {
            LoadAndInitializeAccounts();
        }

        private void LoadAndInitializeAccounts()
        {
            List<SteamAccount> accounts = [];

            if (File.Exists("steam-accounts.json"))
            {
                try
                {
                    var json = File.ReadAllText("steam-accounts.json");
                    var loadedAccounts = JsonSerializer.Deserialize<List<SteamAccount>>(json);
                    if (loadedAccounts != null && loadedAccounts.Count > 0)
                    {
                        accounts.AddRange(loadedAccounts);
                        Console.WriteLine($"Loaded {accounts.Count} Steam accounts from steam-accounts.json");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading steam-accounts.json: {ex.Message}");
                }
            }

            // Fallback to environment variables if no accounts loaded from JSON
            if (accounts.Count == 0)
            {
                var steamUsername = Environment.GetEnvironmentVariable("STEAM_USERNAME");
                var steamPassword = Environment.GetEnvironmentVariable("STEAM_PASSWORD");
                if (!string.IsNullOrEmpty(steamUsername) && !string.IsNullOrEmpty(steamPassword))
                {
                    accounts.Add(new SteamAccount { Username = steamUsername, Password = steamPassword });
                    Console.WriteLine("Using Steam account from environment variables");
                }
            }

            if (accounts.Count == 0)
            {
                throw new InvalidOperationException("No Steam accounts configured. Please provide steam-accounts.json or set STEAM_USERNAME/STEAM_PASSWORD environment variables.");
            }

            // Create account managers
            foreach (var account in accounts)
            {
                var manager = new SteamAccountManager(account);
                _accountManagers.Add(manager);

                // Subscribe to callbacks for each account
                manager.Manager.Subscribe<SteamClient.ConnectedCallback>((callback) => OnConnected(callback, manager));
                manager.Manager.Subscribe<SteamClient.DisconnectedCallback>((callback) => OnDisconnected(callback, manager));
                manager.Manager.Subscribe<SteamUser.LoggedOnCallback>((callback) => OnLoggedOn(callback, manager));
                manager.Manager.Subscribe<SteamUser.LoggedOffCallback>((callback) => OnLoggedOff(callback, manager));
                manager.Manager.Subscribe<SteamGameCoordinator.MessageCallback>((callback) => OnGCMessage(callback, manager));
            }
        }

        public async Task<CEconItemPreviewDataBlock?> GetItemInfoAsync(ulong s, ulong a, ulong d, ulong m)
        {
            if (_accountManagers.Count == 0)
            {
                throw new InvalidOperationException("No Steam accounts configured");
            }

            if (!_isRunning)
            {
                Console.WriteLine("Steam service not running, connecting...");
                await ConnectAsync();
            }

            var jobId = a; // Use A (itemid) parameter as Job ID

            // RunContinuationsAsynchronously so that the SetResult in ResolvePendingRequest (called
            // on the GC callback thread while holding the pending list's lock) never runs a waiter's
            // continuation inline under that lock.
            var tcs = new TaskCompletionSource<CEconItemPreviewDataBlock?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Coalesce concurrent lookups of the same itemid: the first caller creates the pending
            // list and becomes the leader that drives the GC request(s); everyone else joins the
            // list and just waits for the leader's response. The join is atomic, so - unlike a
            // check-then-act on ContainsKey - a caller can never slip past the check just as the
            // response removes the entry and then orphan itself on a list nobody drives.
            if (!JoinOrCreatePendingRequest(jobId, tcs))
            {
                Console.WriteLine($"Request for itemid {jobId} already pending, waiting for existing request...");

                // Bound the wait: if the leader never completes, return null instead of hanging
                // forever. The window covers the leader's own 2s-per-account retry budget plus slack.
                var coalescedTimeout = Task.Delay(TimeSpan.FromSeconds(10));
                if (await Task.WhenAny(tcs.Task, coalescedTimeout) == coalescedTimeout)
                {
                    RemovePendingRequest(jobId, tcs);
                    Console.WriteLine($"Coalesced wait for itemid {jobId} timed out");
                    return null;
                }
                return await tcs.Task;
            }

            // Leader: drive the request across up to 3 accounts, all resolving the one tcs. On any
            // exit the leader's tcs is pulled from the pending list; a late GC response then finds
            // the entry already gone and is simply logged.
            try
            {
                var maxRetries = Math.Min(3, _accountManagers.Count);
                var attemptedAccounts = new HashSet<int>();

                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    var accountManager = GetNextAvailableAccount(attemptedAccounts);
                    if (accountManager == null)
                    {
                        Console.WriteLine("No available accounts for request");
                        break;
                    }

                    attemptedAccounts.Add(_accountManagers.IndexOf(accountManager));

                    if (!accountManager.IsConnected || !accountManager.IsLoggedIn)
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] Account not ready, trying next account...");
                        continue;
                    }

                    try
                    {
                        await SendGCRequest(accountManager, s, a, d, m, jobId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] Failed to send GC request: {ex.Message}");
                        continue; // try next account
                    }

                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
                    if (await Task.WhenAny(tcs.Task, timeoutTask) != timeoutTask)
                    {
                        return await tcs.Task; // GC responded (success or null item)
                    }

                    Console.WriteLine($"[{accountManager.Account.Username}] Request timed out for job {jobId}, trying next account...");
                }

                Console.WriteLine($"All account attempts failed for itemid {jobId}");
                return null;
            }
            finally
            {
                RemovePendingRequest(jobId, tcs);
            }
        }

        // Atomically join jobId's pending-waiter list, creating it when this is the first caller.
        // Returns true when this caller created the list (and is therefore the leader that must
        // drive the GC request); false when it joined an in-flight request as a waiter.
        //
        // The list is re-checked under its own lock so a waiter can never attach to a list that
        // ResolvePendingRequest has already pulled from the dictionary: such a caller finds the
        // entry stale and loops to create a fresh leader entry instead of orphaning its TCS on a
        // list nobody will resolve (which would stall it to a spurious timeout).
        internal bool JoinOrCreatePendingRequest(ulong jobId, TaskCompletionSource<CEconItemPreviewDataBlock?> tcs)
        {
            var newList = new List<TaskCompletionSource<CEconItemPreviewDataBlock?>> { tcs };
            while (true)
            {
                var list = _pendingRequests.GetOrAdd(jobId, newList);
                if (ReferenceEquals(list, newList))
                {
                    return true; // we created it -> leader
                }

                lock (list)
                {
                    // Attach only while this list is still the live dictionary entry. Resolution
                    // removes the entry before resolving under this same lock, so a stale list
                    // means the request already completed and we must retry as a new leader.
                    if (_pendingRequests.TryGetValue(jobId, out var current) && ReferenceEquals(current, list))
                    {
                        list.Add(tcs);
                        return false; // joined as waiter
                    }
                }
                // Stale list; loop and retry.
            }
        }

        // Removes one waiter from a job's pending list, dropping the job entry when it was the last.
        internal void RemovePendingRequest(ulong jobId, TaskCompletionSource<CEconItemPreviewDataBlock?> tcs)
        {
            if (_pendingRequests.TryGetValue(jobId, out var list))
            {
                lock (list)
                {
                    list.Remove(tcs);
                    if (list.Count == 0)
                    {
                        _pendingRequests.TryRemove(jobId, out _);
                    }
                }
            }
        }

        // Resolves every waiter on a job's pending list with the GC's result. Removal is atomic
        // with resolution: the list is pulled from the dictionary *first* and only then resolved
        // under its lock, so a waiter joining concurrently either attaches before the removal (and
        // is resolved here) or finds the entry gone and retries as a fresh leader - it can never be
        // orphaned on a list about to be dropped. TrySetResult (not SetResult) keeps a duplicate
        // response, or a waiter already completed by a coalesced-timeout, from throwing and killing
        // the callback loop.
        internal void ResolvePendingRequest(ulong itemId, CEconItemPreviewDataBlock? item)
        {
            if (_pendingRequests.TryRemove(itemId, out var pendingList))
            {
                lock (pendingList)
                {
                    foreach (var tcs in pendingList)
                    {
                        tcs.TrySetResult(item);
                    }
                }
            }
        }

        private SteamAccountManager? GetNextAvailableAccount(HashSet<int> attemptedAccounts)
        {
            // Round-robin selection, but skip already attempted accounts. Concurrent callers read
            // and advance _currentAccountIndex, so the read-modify-write is locked - otherwise two
            // requests can pick the same account and defeat the rotation and per-account throttle.
            lock (_accountSelectionLock)
            {
                for (int i = 0; i < _accountManagers.Count; i++)
                {
                    var index = (_currentAccountIndex + i) % _accountManagers.Count;
                    if (!attemptedAccounts.Contains(index))
                    {
                        _currentAccountIndex = (index + 1) % _accountManagers.Count;
                        return _accountManagers[index];
                    }
                }
                return null;
            }
        }

        private static async Task SendGCRequest(SteamAccountManager accountManager, ulong s, ulong a, ulong d, ulong m, ulong jobId)
        {
            await accountManager.RateLimitSemaphore.WaitAsync();
            try
            {
                var timeSinceLastRequest = DateTime.UtcNow - accountManager.LastRequestTime;
                var minimumInterval = TimeSpan.FromSeconds(1);

                if (timeSinceLastRequest < minimumInterval)
                {
                    var waitTime = minimumInterval - timeSinceLastRequest;
                    Console.WriteLine($"[{accountManager.Account.Username}] Rate limiting: waiting {waitTime.TotalMilliseconds:F0}ms");
                    await Task.Delay(waitTime);
                }

                accountManager.LastRequestTime = DateTime.UtcNow;

                var request = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockRequest>(
                    (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockRequest);
                request.Body.param_s = s;
                request.Body.param_a = a;
                request.Body.param_d = d;
                request.Body.param_m = m;

                accountManager.GC.Send(request, 730);
                Console.WriteLine($"[{accountManager.Account.Username}] Sent GC request for itemid {jobId}");
            }
            finally
            {
                accountManager.RateLimitSemaphore.Release();
            }
        }

        public async Task ConnectAsync()
        {
            Console.WriteLine($"ConnectAsync called - connecting {_accountManagers.Count} Steam accounts");
            _isRunning = true;

            // Connect all accounts
            var connectionTasks = _accountManagers.Select(ConnectAccount).ToArray();

            // Start callback managers for all accounts
            foreach (var accountManager in _accountManagers)
            {
                _ = Task.Run(() =>
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] Starting callback manager loop");
                    while (_isRunning)
                    {
                        // A throw from a callback handler (e.g. a malformed GC message) must not
                        // escape and kill this loop - that would silently take the account offline
                        // (no logons, reconnects, or GC responses) until the process restarts. Log
                        // and keep pumping.
                        try
                        {
                            accountManager.Manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[{accountManager.Account.Username}] Callback loop error: {ex.Message}");
                        }
                    }
                    Console.WriteLine($"[{accountManager.Account.Username}] Callback manager loop ended");
                });
            }

            // Wait for at least one account to be ready
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < timeout)
            {
                if (_accountManagers.Any(am => am.IsConnected && am.IsLoggedIn))
                {
                    Console.WriteLine("At least one Steam account connected successfully");
                    return;
                }
                Console.WriteLine("Waiting for account connections...");
                await Task.Delay(1000);
            }

            var connectedCount = _accountManagers.Count(am => am.IsConnected && am.IsLoggedIn);
            if (connectedCount == 0)
            {
                throw new Exception("Failed to connect any Steam accounts");
            }

            Console.WriteLine($"Steam service started with {connectedCount}/{_accountManagers.Count} accounts connected");
        }

        private async Task ConnectAccount(SteamAccountManager accountManager)
        {
            try
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Connecting account");
                accountManager.Client.Connect();
                await Task.Delay(2000); // Give some time for connection
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Failed to connect account: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            Console.WriteLine("Disconnecting from Steam...");
            _isRunning = false;
            foreach (var accountManager in _accountManagers)
            {
                accountManager.Dispose();
            }
        }

        private void OnConnected(SteamClient.ConnectedCallback callback, SteamAccountManager accountManager)
        {
            Console.WriteLine($"[{accountManager.Account.Username}] Steam client connected");
            accountManager.IsConnected = true;
            _ = LogOnAsync(accountManager); // async auth; don't block the callback thread
        }

        // Steam no longer accepts the legacy username+password logon (it returns InvalidPassword).
        // A cached refresh token logs on directly; otherwise we exchange the credentials for a new
        // token through the authentication service, cache it, and log on with that. No
        // authenticator is supplied, so this covers accounts without Steam Guard; an account that
        // requires a Guard code will throw during the credential exchange.
        private async Task LogOnAsync(SteamAccountManager accountManager)
        {
            var username = accountManager.Account.Username;

            // A token Steam later rejects is dropped in OnLoggedOn, which calls back here; with no
            // cached token this then falls through to a fresh credential auth.
            var cachedToken = _tokenStore.Get(username);
            if (cachedToken != null)
            {
                Console.WriteLine($"[{username}] Logging on with cached token");
                accountManager.UsedCachedToken = true;
                accountManager.User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = username,
                    AccessToken = cachedToken,
                });
                return;
            }

            try
            {
                Console.WriteLine($"[{username}] Authenticating");
                accountManager.UsedCachedToken = false;
                var session = await accountManager.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = username,
                    Password = accountManager.Account.Password,
                    IsPersistentSession = true, // long-lived refresh token we can reuse across restarts
                });

                var poll = await session.PollingWaitForResultAsync();

                // Key by the configured username so the next lookup (also by it) hits.
                _tokenStore.Set(username, poll.RefreshToken);

                Console.WriteLine($"[{username}] Logging on");
                accountManager.User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = poll.AccountName,
                    AccessToken = poll.RefreshToken,
                });
            }
            catch (Exception ex)
            {
                // Bad credentials, or an account that needs a Steam Guard code we don't supply.
                Console.WriteLine($"[{username}] Authentication failed: {ex.Message}");
            }
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback, SteamAccountManager accountManager)
        {
            Console.WriteLine($"[{accountManager.Account.Username}] Steam client disconnected. User initiated: {callback.UserInitiated}");
            accountManager.IsConnected = false;
            accountManager.IsLoggedIn = false;

            if (!callback.UserInitiated && _isRunning)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Disconnection was not user-initiated, attempting to reconnect...");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000); // Wait 5 seconds before reconnecting
                    if (_isRunning && !accountManager.IsConnected)
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] Reconnecting Steam account");
                        accountManager.Client.Connect();
                    }
                });
            }
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback, SteamAccountManager accountManager)
        {
            var username = accountManager.Account.Username;
            Console.WriteLine($"[{username}] Steam logon result: {callback.Result}");
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine($"[{username}] Failed to log on to Steam: {callback.Result}");

                // A cached token Steam rejected (expired, revoked, password changed, ...): drop it
                // and re-authenticate with credentials. The credential path sets UsedCachedToken
                // false, so a failure there just logs and never loops back here.
                if (accountManager.UsedCachedToken)
                {
                    Console.WriteLine($"[{username}] Cached token rejected; re-authenticating with credentials");
                    accountManager.UsedCachedToken = false;
                    _tokenStore.Remove(username);
                    _ = LogOnAsync(accountManager);
                }
                return;
            }

            accountManager.IsLoggedIn = true;

            // Launch CS:GO to connect to game coordinator
            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID(730)
            });
            accountManager.Client.Send(playGame);

            _ = Task.Run(async () =>
            {
                // Wait for CS:GO connection to stabilize before sending Hello
                Console.WriteLine($"[{accountManager.Account.Username}] Waiting 5 seconds for connection to stabilize...");
                await Task.Delay(5000);

                // Send Hello message to GC to establish session
                Console.WriteLine($"[{accountManager.Account.Username}] Sending Hello message to Game Coordinator...");
                var helloMsg = new ClientGCMsgProtobuf<SteamKit2.GC.CSGO.Internal.CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
                helloMsg.Body.version = 2000202; // Protocol version
                accountManager.GC.Send(helloMsg, 730);
            });
        }

        private static void OnLoggedOff(SteamUser.LoggedOffCallback callback, SteamAccountManager accountManager)
        {
            Console.WriteLine($"[{accountManager.Account.Username}] Steam user logged off. Result: {callback.Result}");
            accountManager.IsLoggedIn = false;
        }

        private void OnGCMessage(SteamGameCoordinator.MessageCallback callback, SteamAccountManager accountManager)
        {
            if (callback.AppID != 730) return;

            if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockResponse)
            {
                var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockResponse>(callback.Message);
                var responseItemId = response.Body.iteminfo?.itemid ?? 0;

                if (_pendingRequests.ContainsKey(responseItemId))
                {
                    CEconItemPreviewDataBlock? item = null;
                    if (response.Body.iteminfo != null)
                    {
                        item = response.Body.iteminfo;
                    }
                    else
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] No item info in response");
                    }

                    // Pull the pending list from the dictionary and resolve every waiter on it
                    // atomically (see ResolvePendingRequest).
                    ResolvePendingRequest(responseItemId, item);
                }
                else
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] No pending request found for ItemID: {responseItemId}");
                }
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus)
            {
                var response = new ClientGCMsgProtobuf<CMsgConnectionStatus>(callback.Message);
                Console.WriteLine($"[{accountManager.Account.Username}] GC Connection Status:{response.Body.status}, WaitSeconds:{response.Body.wait_seconds}");

                if (response.Body.status != GCConnectionStatus.GCConnectionStatus_HAVE_SESSION)
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] WARNING: Not properly connected to Game Coordinator!");
                }
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome)
            {
                var response = new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message);
                Console.WriteLine($"[{accountManager.Account.Username}] GC Welcome Received, version: {response.Body.version}");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientLogonFatalError)
            {
                var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientLogonFatalError>(callback.Message);
                Console.WriteLine($"[{accountManager.Account.Username}] ERROR: GC Fatal Logon Error: Code:{response.Body.errorcode}, Message: {response.Body.message}");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_GC2ClientGlobalStats)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] GC Global Stats Received");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchmakingGC2ClientHello)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] GC Hello Received");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientGCRankUpdate)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] GC Rank Update Received");
            }
            else
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Unhandled GC Message Received: {callback.EMsg}");
            }
        }
    }
}
