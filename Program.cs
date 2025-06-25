using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using SteamKit2;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using CSGOSkinAPI.Services;
using CSGOSkinAPI.Models;
using ProtoBuf;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/javascript", "text/css", "text/html", "text/json", "text/plain"]);
});
builder.Services.AddSingleton<SteamService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<ConstDataService>();

var app = builder.Build();

app.UseResponseCompression();
app.UseDefaultFiles(); // Must be before UseStaticFiles
app.UseStaticFiles();

app.UseRouting();
app.MapControllers();

// Initialize database on startup
var dbService = app.Services.GetRequiredService<DatabaseService>();
await dbService.InitializeDatabaseAsync();

// Initialize Steam connection
var steamService = app.Services.GetRequiredService<SteamService>();
_ = steamService.ConnectAsync();

// Initialize ConstDataService (loads const.json)
var constDataService = app.Services.GetRequiredService<ConstDataService>();

// Handle Ctrl-C gracefully
Console.CancelKeyPress += (sender, e) =>
{
    Console.WriteLine("\nReceived Ctrl-C, disconnecting from Steam...");
    steamService.Disconnect();
    e.Cancel = false;
};

app.Run();

namespace CSGOSkinAPI.Controllers
{
    [ApiController]
    [Route("api")]
    public partial class SkinController(SteamService steamService, DatabaseService dbService, ConstDataService constDataService) : ControllerBase
    {
        [GeneratedRegex(@"steam://rungame/730/76561202255233023/ csgo_econ_action_preview ([SM])(\d+)A(\d+)D(\d+)", RegexOptions.Compiled)]
        private static partial Regex InspectUrlRegex();
        [GeneratedRegex(@"steam://rungame/730/76561202255233023/ csgo_econ_action_preview ([0-9A-F]+)", RegexOptions.Compiled)]
        private static partial Regex InspectUrlHexRegex();

        [HttpGet]
        public async Task<IActionResult> GetSkinData([FromQuery] string? url,
            [FromQuery] ulong s = 0, [FromQuery] ulong a = 0,
            [FromQuery] ulong d = 0, [FromQuery] ulong m = 0)
        {
            try
            {
                if (!string.IsNullOrEmpty(url))
                {
                    var parsed = ParseInspectUrl(url);
                    if (parsed == null)
                    {
                        Console.WriteLine("Failed to parse inspect URL");
                        return BadRequest(new { error = "Invalid inspect URL format" });
                    }

                    (s, a, d, m, var directItem) = parsed.Value;

                    if (directItem != null)
                    {
                        constDataService.FinalizeAttributes(directItem);
                        return Ok(CreateResponse(directItem, s, a, d, m));
                    }
                }

                var existingItem = await dbService.GetItemAsync(a);
                if (existingItem != null)
                {
                    constDataService.FinalizeAttributes(existingItem);
                    return Ok(CreateResponse(existingItem, s, a, d, m));
                }

                var itemInfo = await steamService.GetItemInfoAsync(s, a, d, m);
                if (itemInfo == null)
                {
                    Console.WriteLine("Item not found in Steam GC");
                    return NotFound(new { error = "Steam GC did not return an item" });
                }

                await dbService.SaveItemWithExtrasAsync(itemInfo);
                constDataService.FinalizeAttributes(itemInfo);
                return Ok(CreateResponse(itemInfo, s, a, d, m));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetSkinData: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        private static (ulong s, ulong a, ulong d, ulong m, ItemInfo? directItem)? ParseInspectUrl(string url)
        {
            var decodedUrl = HttpUtility.UrlDecode(url);
            var match = InspectUrlRegex().Match(decodedUrl);
            if (!match.Success)
            {
                var hexMatch = InspectUrlHexRegex().Match(decodedUrl);
                if (!hexMatch.Success)
                {
                    Console.WriteLine($"Failed to decode URL: {url}");
                    return null;
                }
                // Read the bytes, dropping the leading null byte and the trailing 4 checksum bytes
                var hexBytes = Convert.FromHexString(hexMatch.Groups[1].Value)[1..^4];
                var itemInfoProto = Serializer.Deserialize<CEconItemPreviewDataBlock>(new MemoryStream(hexBytes));
                var itemInfo = SteamService.CreateItemInfoFromPreviewData(itemInfoProto);
                return (0, itemInfo.ItemId, 0, 0, itemInfo);
            }

            ulong s = 0, a, d, m = 0;
            var firstParam = match.Groups[1].Value;
            var firstValue = ulong.Parse(match.Groups[2].Value);
            if (firstParam == "S")
            {
                s = firstValue;
            }
            else if (firstParam == "M")
            {
                m = firstValue;
            }
            a = ulong.Parse(match.Groups[3].Value);
            d = ulong.Parse(match.Groups[4].Value);
            return (s, a, d, m, null);
        }

        private static object CreateResponse(ItemInfo item, ulong s, ulong a, ulong d, ulong m)
        {
            return new
            {
                itemid = item.ItemId,
                defindex = item.DefIndex,
                paintindex = item.PaintIndex,
                rarity = item.Rarity,
                quality = item.Quality,
                paintwear = item.PaintWear,
                paintseed = item.PaintSeed,
                inventory = item.Inventory,
                origin = item.Origin,
                stattrak = item.StatTrak,
                special = item.Special,
                weapon = item.Weapon,
                skin = item.Skin,
                stickers = item.Stickers.Select(s => new
                {
                    slot = s.Slot,
                    sticker_id = s.StickerId,
                    wear = s.Wear
                }).ToArray(),
                keychains = item.Keychains.Select(k => new
                {
                    slot = k.Slot,
                    sticker_id = k.StickerId,
                    wear = k.Wear
                }).ToArray(),
                s,
                a,
                d,
                m
            };
        }
    }
}

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

    public class SteamService
    {
        private readonly List<SteamAccountManager> _accountManagers = [];
        private bool _isRunning = false;
        private readonly ConcurrentDictionary<ulong, List<TaskCompletionSource<ItemInfo?>>> _pendingRequests = new();
        private int _currentAccountIndex = 0;

        public static ItemInfo CreateItemInfoFromPreviewData(CEconItemPreviewDataBlock itemInfoProto)
        {
            var paintwear = BitConverter.ToSingle(BitConverter.GetBytes(itemInfoProto.paintwear), 0);
            return new ItemInfo
            {
                ItemId = itemInfoProto.itemid,
                DefIndex = (int)itemInfoProto.defindex,
                PaintIndex = (int)itemInfoProto.paintindex,
                Rarity = (int)itemInfoProto.rarity,
                Quality = (int)itemInfoProto.quality,
                PaintWear = paintwear,
                PaintSeed = (int)itemInfoProto.paintseed,
                Inventory = itemInfoProto.inventory,
                Origin = (int)itemInfoProto.origin,
                StatTrak = itemInfoProto.ShouldSerializekilleatervalue(),
                Stickers = CreateStickersFromProto(itemInfoProto.itemid, itemInfoProto.stickers),
                Keychains = CreateStickersFromProto(itemInfoProto.itemid, itemInfoProto.keychains)
            };
        }

        public static List<Sticker> CreateStickersFromProto(ulong itemid, List<CEconItemPreviewDataBlock.Sticker> stickerProtos)
        {
            var stickers = new List<Sticker>();
            foreach (var stickerProto in stickerProtos)
            {
                Console.WriteLine(stickerProto.ShouldSerializewear());
                stickers.Add(new Sticker
                {
                    ItemId = itemid,
                    Slot = stickerProto.slot,
                    StickerId = stickerProto.sticker_id,
                    Wear = stickerProto.wear,
                    Scale = stickerProto.ShouldSerializescale() ? stickerProto.scale : null,
                    Rotation = stickerProto.ShouldSerializerotation() ? stickerProto.rotation : null,
                    TintId = stickerProto.ShouldSerializetint_id() ? stickerProto.tint_id : null,
                    OffsetX = stickerProto.ShouldSerializeoffset_x() ? stickerProto.offset_x : null,
                    OffsetY = stickerProto.ShouldSerializeoffset_y() ? stickerProto.offset_y : null,
                    OffsetZ = stickerProto.ShouldSerializeoffset_z() ? stickerProto.offset_z : null,
                    Pattern = stickerProto.ShouldSerializepattern() ? stickerProto.pattern : null,
                    HighlightReel = stickerProto.ShouldSerializehighlight_reel() ? stickerProto.highlight_reel : null
                });
            }
            return stickers;
        }

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

        public async Task<ItemInfo?> GetItemInfoAsync(ulong s, ulong a, ulong d, ulong m)
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

            // Check if request is already pending
            if (_pendingRequests.ContainsKey(jobId))
            {
                Console.WriteLine($"Request for itemid {jobId} already pending, waiting for existing request...");
                var tcs = new TaskCompletionSource<ItemInfo?>();
                _pendingRequests.AddOrUpdate(jobId,
                    [tcs],
                    (key, existingList) =>
                    {
                        lock (existingList)
                        {
                            existingList.Add(tcs);
                        }
                        return existingList;
                    });
                return await tcs.Task;
            }

            // Try up to 3 accounts or all available accounts
            var maxRetries = Math.Min(3, _accountManagers.Count);
            var attemptedAccounts = new HashSet<int>();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var accountManager = GetNextAvailableAccount(attemptedAccounts);
                if (accountManager == null)
                {
                    Console.WriteLine("No available accounts for request");
                    return null;
                }

                attemptedAccounts.Add(_accountManagers.IndexOf(accountManager));

                if (!accountManager.IsConnected || !accountManager.IsLoggedIn)
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] Account not ready, trying next account...");
                    continue;
                }

                var result = await TryGCRequestWithAccount(accountManager, s, a, d, m, jobId);
                if (result != null)
                {
                    return result; // Success
                }

                Console.WriteLine($"[{accountManager.Account.Username}] Request timed out for job {jobId}, trying next account...");
            }

            Console.WriteLine($"All {maxRetries} account attempts failed for itemid {jobId}");
            return null;
        }

        private async Task<ItemInfo?> TryGCRequestWithAccount(SteamAccountManager accountManager, ulong s, ulong a, ulong d, ulong m, ulong jobId)
        {
            var tcs = new TaskCompletionSource<ItemInfo?>();

            _pendingRequests.AddOrUpdate(jobId,
                [tcs],
                (key, existingList) =>
                {
                    lock (existingList)
                    {
                        existingList.Add(tcs);
                    }
                    return existingList;
                });

            try
            {
                await SendGCRequest(accountManager, s, a, d, m, jobId);

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Clean up this request from pending list
                    if (_pendingRequests.TryGetValue(jobId, out var timedOutList))
                    {
                        lock (timedOutList)
                        {
                            timedOutList.Remove(tcs);
                            if (timedOutList.Count == 0)
                            {
                                _pendingRequests.TryRemove(jobId, out _);
                            }
                        }
                    }

                    return null; // Timeout - will try next account
                }

                return await tcs.Task; // Success or GC returned null
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Failed to send GC request: {ex.Message}");

                // Clean up this request from pending list
                if (_pendingRequests.TryGetValue(jobId, out var failedList))
                {
                    lock (failedList)
                    {
                        failedList.Remove(tcs);
                        if (failedList.Count == 0)
                        {
                            _pendingRequests.TryRemove(jobId, out _);
                        }
                    }
                }

                return null; // Exception - will try next account
            }
        }

        private SteamAccountManager? GetNextAvailableAccount(HashSet<int> attemptedAccounts)
        {
            // Round-robin selection, but skip already attempted accounts
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

        private async Task SendGCRequest(SteamAccountManager accountManager, ulong s, ulong a, ulong d, ulong m, ulong jobId)
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
                        accountManager.Manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
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

            Console.WriteLine($"[{accountManager.Account.Username}] Logging on");
            accountManager.User.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountManager.Account.Username,
                Password = accountManager.Account.Password
            });
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
            Console.WriteLine($"[{accountManager.Account.Username}] Steam logon result: {callback.Result}");
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Failed to log on to Steam: {callback.Result}");
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

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback, SteamAccountManager accountManager)
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

                if (_pendingRequests.TryGetValue(responseItemId, out var pendingList))
                {
                    ItemInfo? item = null;
                    if (response.Body.iteminfo != null)
                    {
                        item = CreateItemInfoFromPreviewData(response.Body.iteminfo);
                    }
                    else
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] No item info in response");
                    }

                    // Resolve all pending requests for this itemid
                    lock (pendingList)
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] Resolving {pendingList.Count} pending requests for itemid {responseItemId}");
                        foreach (var tcs in pendingList)
                        {
                            tcs.SetResult(item);
                        }
                    }
                    _pendingRequests.TryRemove(responseItemId, out _);
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

    public class DatabaseService
    {
        private const string ConnectionString = "Data Source=searches.db;foreign keys=true;";

        public async Task InitializeDatabaseAsync()
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            var createTableCommand = @"
                CREATE TABLE IF NOT EXISTS searches (
                    itemid INTEGER PRIMARY KEY NOT NULL,
                    defindex INTEGER NOT NULL,
                    paintindex INTEGER NOT NULL,
                    rarity INTEGER NOT NULL,
                    quality INTEGER NOT NULL,
                    paintwear REAL NOT NULL,
                    paintseed INTEGER NOT NULL,
                    inventory INTEGER NOT NULL,
                    origin INTEGER NOT NULL,
                    stattrak INTEGER NOT NULL
                )";

            using var command = new SqliteCommand(createTableCommand, connection);
            await command.ExecuteNonQueryAsync();

            foreach (var tableName in new[] { "stickers", "keychains" })
            {
                var createStickerTableCommand = @$"
                    CREATE TABLE IF NOT EXISTS {tableName} (
                        itemid INTEGER NOT NULL,
                        slot INTEGER NOT NULL,
                        sticker_id INTEGER NOT NULL,
                        wear REAL NOT NULL,
                        scale REAL,
                        rotation REAL,
                        tint_id INTEGER,
                        offset_x REAL,
                        offset_y REAL,
                        offset_z REAL,
                        pattern INTEGER,
                        highlight_reel INTEGER,
                        FOREIGN KEY (itemid) REFERENCES searches(itemid) ON DELETE CASCADE
                )";

                using var stickersCommand = new SqliteCommand(createStickerTableCommand, connection);
                await stickersCommand.ExecuteNonQueryAsync();

                var createIndexCommand = @$"CREATE INDEX IF NOT EXISTS itemid on {tableName} (itemid)";
                using var indexCommand = new SqliteCommand(createIndexCommand, connection);
                await indexCommand.ExecuteNonQueryAsync();
            }
        }

        public async Task<List<Sticker>> GetStickersAsync(ulong itemId, bool stickersTable)
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string stickersQuery = "SELECT * FROM stickers WHERE itemid = @itemid ORDER BY slot";
            const string keychainsQuery = "SELECT * FROM keychains WHERE itemid = @itemid ORDER BY slot";
            var query = stickersTable ? stickersQuery : keychainsQuery;
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@itemid", (long)itemId);

            var stickers = new List<Sticker>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                stickers.Add(new Sticker
                {
                    ItemId = (ulong)reader.GetInt64(reader.GetOrdinal("itemid")),
                    Slot = (uint)reader.GetInt32(reader.GetOrdinal("slot")),
                    StickerId = (uint)reader.GetInt32(reader.GetOrdinal("sticker_id")),
                    Wear = reader.GetFloat(reader.GetOrdinal("wear")),
                    Scale = reader.IsDBNull(reader.GetOrdinal("scale")) ? null : reader.GetFloat(reader.GetOrdinal("scale")),
                    Rotation = reader.IsDBNull(reader.GetOrdinal("rotation")) ? null : reader.GetFloat(reader.GetOrdinal("rotation")),
                    TintId = reader.IsDBNull(reader.GetOrdinal("tint_id")) ? null : (uint)reader.GetInt32(reader.GetOrdinal("tint_id")),
                    OffsetX = reader.IsDBNull(reader.GetOrdinal("offset_x")) ? null : reader.GetFloat(reader.GetOrdinal("offset_x")),
                    OffsetY = reader.IsDBNull(reader.GetOrdinal("offset_y")) ? null : reader.GetFloat(reader.GetOrdinal("offset_y")),
                    OffsetZ = reader.IsDBNull(reader.GetOrdinal("offset_z")) ? null : reader.GetFloat(reader.GetOrdinal("offset_z")),
                    Pattern = reader.IsDBNull(reader.GetOrdinal("pattern")) ? null : (uint)reader.GetInt32(reader.GetOrdinal("pattern")),
                    HighlightReel = reader.IsDBNull(reader.GetOrdinal("highlight_reel")) ? null : (uint)reader.GetInt32(reader.GetOrdinal("highlight_reel"))
                });
            }

            return stickers;
        }

        public async Task<ItemInfo?> GetItemAsync(ulong itemId)
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            var query = "SELECT * FROM searches WHERE itemid = @itemid";
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@itemid", itemId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var item = new ItemInfo
                {
                    ItemId = (ulong)reader.GetInt64(reader.GetOrdinal("itemid")),
                    DefIndex = reader.GetInt32(reader.GetOrdinal("defindex")),
                    PaintIndex = reader.GetInt32(reader.GetOrdinal("paintindex")),
                    Rarity = reader.GetInt32(reader.GetOrdinal("rarity")),
                    Quality = reader.GetInt32(reader.GetOrdinal("quality")),
                    PaintWear = reader.GetDouble(reader.GetOrdinal("paintwear")),
                    PaintSeed = reader.GetInt32(reader.GetOrdinal("paintseed")),
                    Inventory = reader.GetInt64(reader.GetOrdinal("inventory")),
                    Origin = reader.GetInt32(reader.GetOrdinal("origin")),
                    StatTrak = reader.GetInt32(reader.GetOrdinal("stattrak")) == 1,
                    Stickers = await GetStickersAsync(itemId, true),
                    Keychains = await GetStickersAsync(itemId, false)
                };

                return item;
            }

            return null;
        }

        public async Task SaveItemAsync(ItemInfo item)
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            var insert = @"
                INSERT OR REPLACE INTO searches 
                (itemid, defindex, paintindex, rarity, quality, paintwear, paintseed, inventory, origin, stattrak)
                VALUES (@itemid, @defindex, @paintindex, @rarity, @quality, @paintwear, @paintseed, @inventory, @origin, @stattrak)";

            using var command = new SqliteCommand(insert, connection);
            command.Parameters.AddWithValue("@itemid", (long)item.ItemId);
            command.Parameters.AddWithValue("@defindex", item.DefIndex);
            command.Parameters.AddWithValue("@paintindex", item.PaintIndex);
            command.Parameters.AddWithValue("@rarity", item.Rarity);
            command.Parameters.AddWithValue("@quality", item.Quality);
            command.Parameters.AddWithValue("@paintwear", item.PaintWear);
            command.Parameters.AddWithValue("@paintseed", item.PaintSeed);
            command.Parameters.AddWithValue("@inventory", item.Inventory);
            command.Parameters.AddWithValue("@origin", item.Origin);
            command.Parameters.AddWithValue("@stattrak", item.StatTrak ? 1 : 0);

            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveItemWithExtrasAsync(ItemInfo itemInfo)
        {
            await SaveItemAsync(itemInfo);

            if (itemInfo.Stickers?.Count > 0)
            {
                await SaveStickersAsync(itemInfo.ItemId, itemInfo.Stickers, true);
            }

            if (itemInfo.Keychains?.Count > 0)
            {
                await SaveStickersAsync(itemInfo.ItemId, itemInfo.Keychains, false);
            }
        }

        private async Task SaveStickersAsync(ulong itemId, List<Sticker> items, bool stickerTable)
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string insertSchema = @"
                (itemid, slot, sticker_id, wear, scale, rotation, tint_id, offset_x, offset_y, offset_z, pattern, highlight_reel) VALUES
                (@itemid, @slot, @sticker_id, @wear, @scale, @rotation, @tint_id, @offset_x, @offset_y, @offset_z, @pattern, @highlight_reel)";
            const string insertStickersQuery = @"INSERT INTO stickers " + insertSchema;
            const string insertKeychainsQuery = @"INSERT INTO keychains " + insertSchema;

            var insertQuery = stickerTable ? insertStickersQuery : insertKeychainsQuery;

            foreach (var item in items)
            {
                using var insertCommand = new SqliteCommand(insertQuery, connection);
                insertCommand.Parameters.AddWithValue("@itemid", (long)itemId);
                insertCommand.Parameters.AddWithValue("@slot", item.Slot);
                insertCommand.Parameters.AddWithValue("@sticker_id", item.StickerId);
                insertCommand.Parameters.AddWithValue("@wear", item.Wear);
                insertCommand.Parameters.AddWithValue("@scale", item.Scale != null ? item.Scale : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@rotation", item.Rotation != null ? item.Rotation : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@tint_id", item.TintId != null ? item.TintId : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_x", item.OffsetX != null ? item.OffsetX : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_y", item.OffsetY != null ? item.OffsetY : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_z", item.OffsetZ != null ? item.OffsetZ : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@pattern", item.Pattern != null ? item.Pattern : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@highlight_reel", item.HighlightReel != null ? item.HighlightReel : DBNull.Value);
                await insertCommand.ExecuteNonQueryAsync();
            }
        }
    }

    public class ConstDataService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ConstData _constData;

        public ConstDataService()
        {
            var jsonString = File.ReadAllText("const.json");
            _constData = JsonSerializer.Deserialize<ConstData>(jsonString, JsonOptions) ?? new ConstData();
        }

        public void FinalizeAttributes(ItemInfo item)
        {
            var weaponType = GetWeaponName(item.DefIndex);
            var pattern = GetPatternName(item.PaintIndex);
            var paintseed = item.PaintSeed;
            var paintindex = item.PaintIndex;

            item.Weapon = weaponType;
            item.Skin = pattern;

            var special = "";

            if (pattern == "Marble Fade" && _constData.Fireice?.Contains(weaponType) == true)
            {
                special = ConstData.FireIceNames[_constData.FireiceOrder![paintseed]];
            }
            else if (pattern == "Fade" && _constData.Fades?.ContainsKey(weaponType) == true)
            {
                special = GetFadePercent(paintseed, _constData.Fades[weaponType]) + "%";
            }
            else if (pattern == "Amber Fade" && _constData.AmberFades?.ContainsKey(weaponType) == true)
            {
                special = GetFadePercent(paintseed, _constData.AmberFades[weaponType]) + "%";
            }
            else if ((pattern == "Doppler" || pattern == "Gamma Doppler") && _constData.Doppler?.ContainsKey(paintindex.ToString()) == true)
            {
                special = _constData.Doppler[paintindex.ToString()];
            }
            else if (pattern == "Crimson Kimono" && _constData.Kimonos?.ContainsKey(paintseed.ToString()) == true)
            {
                special = _constData.Kimonos[paintseed.ToString()];
            }

            item.Special = special;
        }

        private double GetFadePercent(int paintseed, bool reversed)
        {
            const int minimumFadePercent = 80;
            var fadeIndex = _constData.FadeOrder![paintseed];
            if (reversed)
            {
                fadeIndex = 1000 - fadeIndex;
            }
            var actualFadePercent = (double)fadeIndex / 1001;
            var scaledFadePercent = Math.Round(minimumFadePercent + actualFadePercent * (100 - minimumFadePercent), 1);
            return scaledFadePercent;
        }

        private string GetWeaponName(int defIndex)
        {
            if (_constData.Items?.TryGetValue(defIndex.ToString(), out var weapon) == true)
            {
                return weapon;
            }

            Console.WriteLine($"Item {defIndex} is missing from constants");
            return "";
        }

        private string GetPatternName(int paintIndex)
        {
            if (_constData.Skins?.TryGetValue(paintIndex.ToString(), out var pattern) == true)
            {
                return pattern;
            }

            Console.WriteLine($"Skin {paintIndex} is missing from constants");
            return "";
        }
    }
}

namespace CSGOSkinAPI.Models
{
    public class SteamAccount
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public class ConstData
    {
        public Dictionary<string, string>? Items { get; set; }
        public Dictionary<string, string>? Skins { get; set; }
        public Dictionary<string, bool>? Fades { get; set; }
        public Dictionary<string, bool>? AmberFades { get; set; }
        [JsonPropertyName("fade_order")]
        public int[]? FadeOrder { get; set; }
        public string[]? Fireice { get; set; }
        [JsonPropertyName("fireice_order")]
        public int[]? FireiceOrder { get; set; }
        public Dictionary<string, string>? Doppler { get; set; }
        public Dictionary<string, string>? Kimonos { get; set; }

        public static readonly string[] FireIceNames = ["", "1st Max", "2nd Max", "3rd Max", "4th Max", "5th Max", "6th Max", "7th Max", "8th Max", "9th Max", "10th Max", "FFI"];

    }

    public class Sticker
    {
        public ulong ItemId { get; set; } // Foreign key to ItemInfo
        public uint Slot { get; set; }
        public uint StickerId { get; set; }
        public float Wear { get; set; }
        public float? Scale { get; set; }
        public float? Rotation { get; set; }
        public uint? TintId { get; set; }
        public float? OffsetX { get; set; }
        public float? OffsetY { get; set; }
        public float? OffsetZ { get; set; }
        public uint? Pattern { get; set; }
        public uint? HighlightReel { get; set; }
    }

    public class ItemInfo
    {
        public ulong ItemId { get; set; }
        public int DefIndex { get; set; }
        public int PaintIndex { get; set; }
        public int Rarity { get; set; }
        public int Quality { get; set; }
        public double PaintWear { get; set; }
        public int PaintSeed { get; set; }
        public long Inventory { get; set; }
        public int Origin { get; set; }
        public bool StatTrak { get; set; }
        public string? Special { get; set; }
        public string? Weapon { get; set; }
        public string? Skin { get; set; }
        public List<Sticker> Stickers { get; set; } = [];
        public List<Sticker> Keychains { get; set; } = [];
    }
}
