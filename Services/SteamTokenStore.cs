namespace CSGOSkinAPI.Services
{
    // Persists the long-lived refresh token Steam hands back after a credential login, keyed by
    // configured username, so restarts can log on with the token instead of re-sending the
    // password (and re-prompting for any Steam Guard). A plain JSON file, gitignored like
    // steam-accounts.json - a refresh token is itself a credential. All access is locked since
    // each account logs on from its own thread.
    public class SteamTokenStore
    {
        private readonly string _path;
        private readonly object _lock = new();
        private readonly ILogger _logger;

        // Not a DI service - SteamService news one up for the file it owns - so the logger is
        // passed in rather than injected, and defaults to the null sink for the tests that
        // construct a store directly and assert on the file rather than on the log.
        public SteamTokenStore(string path, ILogger<SteamTokenStore>? logger = null)
        {
            _path = path;
            _logger = logger ?? NullLogger<SteamTokenStore>.Instance;
        }

        public string? Get(string username)
        {
            lock (_lock)
            {
                return Read().GetValueOrDefault(username);
            }
        }

        public void Set(string username, string token)
        {
            lock (_lock)
            {
                var tokens = Read();
                tokens[username] = token;
                Write(tokens);
            }
        }

        public void Remove(string username)
        {
            lock (_lock)
            {
                var tokens = Read();
                if (tokens.Remove(username))
                {
                    Write(tokens);
                }
            }
        }

        private Dictionary<string, string> Read()
        {
            try
            {
                if (File.Exists(_path))
                {
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Missing/corrupt/unreadable: start empty. The next successful login rewrites it.
                _logger.LogWarning(ex, "Could not read Steam token store {TokenStorePath}", _path);
            }
            return [];
        }

        private void Write(Dictionary<string, string> tokens)
        {
            try
            {
                // A refresh token is a credential: anyone holding it can log on as the account
                // without the password or Steam Guard. File.WriteAllText would create the store
                // with the process umask (0644 on a typical host), leaving it readable by every
                // other local user, so create it owner-only instead.
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = OwnerOnly;
                }

                using (var stream = new FileStream(_path, options))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(JsonSerializer.Serialize(tokens));
                }

                // UnixCreateMode only applies when the file is created, so tighten a store that an
                // earlier build (or a restored backup) already left world-readable.
                RestrictToOwner(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A non-writable token store just means we re-auth with credentials next time.
                _logger.LogWarning(ex, "Could not write Steam token store {TokenStorePath}", _path);
            }
        }

        private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        private void RestrictToOwner(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                if (File.GetUnixFileMode(path) != OwnerOnly)
                {
                    File.SetUnixFileMode(path, OwnerOnly);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not restrict permissions on {TokenStorePath}", path);
            }
        }
    }
}
