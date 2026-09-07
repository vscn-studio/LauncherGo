using System.Security.Cryptography;
using System.Text.Json;
using ServerMap.Util;
using Vintagestory.API.Server;

namespace ServerMap.Web;

public sealed class MapAuthStore
{
    public sealed record Principal(string PlayerUid, string PlayerName, bool IsAdmin);
    private sealed record Account(string PlayerUid, string PlayerName, bool IsAdmin, string Salt, string PasswordHash, DateTimeOffset UpdatedAt);
    private sealed record Session(Principal Principal, DateTimeOffset ExpiresAt);
    private readonly string path;
    private readonly object gate = new();
    private Dictionary<string, Account> accounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public MapAuthStore(string path)
    {
        this.path = path;
        try { if (File.Exists(path)) accounts = JsonSerializer.Deserialize<Dictionary<string, Account>>(File.ReadAllText(path)) ?? accounts; }
        catch { accounts = new(StringComparer.Ordinal); }
    }

    public bool SetPassword(IServerPlayer player, string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6) return false;
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var account = new Account(player.PlayerUID, player.PlayerName, player.HasPrivilege("root"), salt, Hash(password, salt), DateTimeOffset.UtcNow);
        lock (gate) { accounts[player.PlayerUID] = account; Save(); }
        return true;
    }

    public (Principal Principal, string SessionId)? Login(string playerName, string password)
    {
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(password)) return null;
        lock (gate)
        {
            var account = accounts.Values.FirstOrDefault(value => string.Equals(value.PlayerName, playerName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (account == null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(account.PasswordHash), Convert.FromHexString(Hash(password, account.Salt)))) return null;
            var principal = new Principal(account.PlayerUid, account.PlayerName, account.IsAdmin);
            var sessionId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            sessions[sessionId] = new Session(principal, DateTimeOffset.UtcNow.AddDays(30));
            return (principal, sessionId);
        }
    }

    public Principal? AuthenticateSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (gate)
        {
            if (!sessions.TryGetValue(sessionId, out var session)) return null;
            if (session.ExpiresAt <= DateTimeOffset.UtcNow) { sessions.Remove(sessionId); return null; }
            return session.Principal;
        }
    }

    public void Logout(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId)) lock (gate) sessions.Remove(sessionId);
    }

    private void Save() => AtomicFile.Replace(path, temp => File.WriteAllText(temp, JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true })));
    private static string Hash(string value, string salt) => Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromHexString(salt), 120_000, HashAlgorithmName.SHA256, 32));
}
