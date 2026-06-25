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

        public SteamTokenStore(string path) => _path = path;

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
                Console.WriteLine($"Could not read {_path}: {ex.Message}");
            }
            return [];
        }

        private void Write(Dictionary<string, string> tokens)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(tokens));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A non-writable token store just means we re-auth with credentials next time.
                Console.WriteLine($"Could not write {_path}: {ex.Message}");
            }
        }
    }
}
