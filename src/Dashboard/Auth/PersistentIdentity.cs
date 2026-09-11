using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AzureFinOps.Dashboard.Auth;

/// <summary>
/// Persistent per-user identity + OAuth refresh-token store backed by an
/// encrypted JSON file under <c>$COPILOT_HOME/users/{oid}/identity.json</c>.
///
/// Why this exists: the ASP.NET <see cref="ISession"/> store is in-memory
/// (<c>AddDistributedMemoryCache</c>) so OAuth tokens vanish on every container
/// restart, forcing users to re-authenticate. We avoid the dependency cost of
/// Redis (and the <strong>file-locking corruption</strong> that breaks SQLite on
/// Azure Files SMB) by writing the long-lived refresh token + a small identity
/// blob to the same persistent <c>/home</c> Azure Files mount the Copilot SDK
/// already uses for chat history, encrypted with ASP.NET Data Protection.
///
/// On the next request after a restart, a hydration middleware reads the
/// signed <c>finops_id</c> cookie (set after successful Entra login), looks up
/// the identity file, and silently mints fresh access tokens via the cached
/// refresh_token. The user never sees a re-auth prompt.
///
/// Security: the file is encrypted with a key from <see cref="IDataProtector"/>
/// scoped to <c>FinOps.Identity.v1</c>; keys persist to
/// <c>/home/dataprotection-keys/</c> so they survive restarts but never leave
/// the tenant. Only refresh tokens are written to disk &#8212; access tokens
/// stay in-memory. The cookie itself contains only an opaque token; the OID is
/// inside the encrypted payload.
/// </summary>
public sealed class PersistentIdentity
{
    private const string IdentityCookieName = "finops_id";

    private static readonly string CopilotHome =
        Environment.GetEnvironmentVariable("COPILOT_HOME")
        ?? Path.Combine(Path.GetTempPath(), "copilot");

    private readonly IDataProtector _protector;
    private readonly IDataProtector _ticketProtector;
    private readonly ILogger<PersistentIdentity> _logger;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ticketLifetime;
    private readonly string _root;

    // Per-oid serialization lock so concurrent SaveIdentity / UpdateRefreshToken
    // / UpdateGraphTier calls can't race on the same file. Cheap: one Semaphore
    // per logged-in user, GC'd implicitly when the dict is rebuilt on restart.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
    private static SemaphoreSlim LockFor(string oid) =>
        _fileLocks.GetOrAdd(oid, _ => new SemaphoreSlim(1, 1));

    // userId → oid lookup so background services (e.g. TenantTokenRefresher)
    // can find an identity record by the userId surfaced in telemetry without
    // an HttpContext. Populated on every Save / Load / Update so once a user has
    // touched the system in this process, lookup is O(1).
    private static readonly ConcurrentDictionary<long, string> _userIdToOid = new();

    // /home is an Azure Files SMB mount and every authenticated request validates the
    // browser ticket against the record, so the decrypted JSON is cached briefly. Caching
    // the text (not the object) keeps callers that mutate RefreshToken off a shared instance.
    private static readonly TimeSpan RecordCacheTtl = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset Expires)> _recordCache = new();

    public PersistentIdentity(IDataProtectionProvider provider, ILogger<PersistentIdentity> logger,
        MicrosoftOAuthOptions? options = null)
        : this(provider, logger, options ?? new MicrosoftOAuthOptions(), TimeProvider.System, CopilotHome) { }

    internal PersistentIdentity(IDataProtectionProvider provider, ILogger<PersistentIdentity> logger,
        MicrosoftOAuthOptions options, TimeProvider clock, string root)
    {
        if (options.AuthenticationLifetimeHours is < 1 or > 168)
            throw new ArgumentOutOfRangeException(nameof(options), "Authentication lifetime must be between 1 and 168 hours.");
        _protector = provider.CreateProtector("FinOps.Identity.v1");
        _ticketProtector = provider.CreateProtector("FinOps.BrowserTicket.v2");
        _logger = logger;
        _clock = clock;
        _ticketLifetime = TimeSpan.FromHours(options.AuthenticationLifetimeHours);
        _root = root;
    }

    /// <summary>SHA-256 of the Entra OID, folded into a 64-bit id. Stable across
    /// devices and sessions for the same human; collisions are astronomically
    /// unlikely (birthday bound > 2^32 OIDs before any expected collision).</summary>
    public static long DeriveUserId(string oid)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(oid));
        return BitConverter.ToInt64(hash, 0);
    }

    /// <summary>Persists identity + the rotating refresh token to disk and
    /// writes the encrypted identity cookie. Call this from the OAuth callback
    /// after a successful id_token validation and from any path that mints a
    /// new refresh_token.</summary>
    public async Task SaveIdentityAsync(HttpContext ctx, IdentityRecord record)
    {
        var sem = LockFor(record.Oid);
        await sem.WaitAsync();
        try
        {
            var dir = GetUserDir(record.Oid);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "identity.json");
            var existing = ReadRecord(record.Oid, useCache: false);
            record.BrowserVersion = !string.IsNullOrEmpty(existing?.BrowserVersion)
                ? existing.BrowserVersion : Guid.NewGuid().ToString("N");
            record.RefreshToken ??= existing?.RefreshToken;
            var encrypted = _protector.Protect(JsonSerializer.Serialize(record));
            AtomicWrite(path, encrypted);
            _recordCache.TryRemove(record.Oid, out _);
            _userIdToOid[record.UserId] = record.Oid;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist identity for oid={Oid}", record.Oid);
            ctx.Session.Clear();
            ctx.Response.Cookies.Delete(IdentityCookieName);
            throw;
        }
        finally { sem.Release(); }

        SetIdentityCookie(ctx, record.Oid);
    }

    /// <summary>Writes (or rewrites) the encrypted <c>finops_id</c> cookie for the
    /// given OID. Also used on Entra account switch to repoint the cookie at the
    /// NEW account when no fresh refresh token came back (the SaveIdentityAsync
    /// path didn't run) — otherwise the stale cookie would resurrect the previous
    /// account's identity on the next hydration.</summary>
    public void SetIdentityCookie(HttpContext ctx, string oid)
    {
        var record = LoadByOid(oid);
        if (record is null || string.IsNullOrEmpty(record.BrowserVersion))
            throw new InvalidOperationException("Browser identity is unavailable.");
        var previous = ReadTicket(ctx);
        var now = _clock.GetUtcNow();
        var ticket = previous is not null && Matches(previous, record)
            ? previous : new BrowserTicket(record.Oid, record.TenantId, record.BrowserVersion, now, now.Add(_ticketLifetime));
        var cookie = _ticketProtector.Protect(JsonSerializer.Serialize(ticket));
        ctx.Response.Cookies.Append(IdentityCookieName, cookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Expires = ticket.ExpiresUtc,
            Path = "/",
        });
        ctx.Session.SetString("browser_authenticated", "1");
    }

    /// <summary>Returns the persisted identity for the OID encoded in the
    /// caller's <c>finops_id</c> cookie, or null if absent / tampered / the
    /// file is missing.</summary>
    public IdentityRecord? Load(HttpContext ctx)
    {
        var ticket = ReadTicket(ctx);
        var record = ticket is not null ? LoadByOid(ticket.Oid) : null;
        if (ticket is not null && record is not null && Matches(ticket, record)) return record;
        if (ctx.Request.Cookies.ContainsKey(IdentityCookieName))
            ctx.Response.Cookies.Delete(IdentityCookieName);
        return null;
    }

    public bool RestoreSession(HttpContext ctx)
    {
        var record = Load(ctx);
        if (record is null)
        {
            if (ctx.Session.GetString("azure_user") is not null || ctx.Session.GetString("browser_authenticated") == "1")
            {
                ctx.Session.Clear();
                ctx.Items["finops.authenticationExpired"] = true;
            }
            return false;
        }

        if (ctx.Session.GetString("azure_user") is { } previousUser)
        {
            try
            {
                var previous = JsonSerializer.Deserialize<JsonElement>(previousUser);
                if (previous.GetProperty("objectId").GetString() != record.Oid || previous.GetProperty("tenantId").GetString() != record.TenantId)
                    ctx.Session.Clear();
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                ctx.Session.Clear();
            }
        }

        ctx.Session.SetString("user", JsonSerializer.Serialize(new
        {
            id = record.UserId, login = $"user-{record.UserId & 0xFFFF:X4}", name = record.Name,
            avatar = (string?)null, email = record.Email
        }));
        ctx.Session.SetString("azure_user", JsonSerializer.Serialize(new
        {
            tenantId = record.TenantId, objectId = record.Oid, name = record.Name, email = record.Email
        }));
        ctx.Session.SetString("browser_authenticated", "1");
        if (!string.IsNullOrEmpty(record.RefreshToken) && ctx.Session.GetString("azure_refresh_token") is null)
            ctx.Session.SetString("azure_refresh_token", record.RefreshToken);
        if (!string.IsNullOrEmpty(record.GraphTier) && ctx.Session.GetString("graph_tier") is null)
            ctx.Session.SetString("graph_tier", record.GraphTier);
        return true;
    }

    private sealed record BrowserTicket(string Oid, string TenantId, string Version, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);

    private BrowserTicket? ReadTicket(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue(IdentityCookieName, out var cookie)) return null;
        try
        {
            var ticket = JsonSerializer.Deserialize<BrowserTicket>(_ticketProtector.Unprotect(cookie));
            var now = _clock.GetUtcNow();
            return ticket is not null && Guid.TryParseExact(ticket.Oid, "D", out _)
                && Guid.TryParseExact(ticket.TenantId, "D", out _) && !string.IsNullOrEmpty(ticket.Version)
                && ticket.IssuedUtc <= now && ticket.ExpiresUtc > now && ticket.ExpiresUtc > ticket.IssuedUtc
                && now - ticket.IssuedUtc < _ticketLifetime ? ticket : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException) { return null; }
    }

    private static bool Matches(BrowserTicket ticket, IdentityRecord record) =>
        ticket.Oid == record.Oid && ticket.TenantId == record.TenantId && ticket.Version == record.BrowserVersion
        && record.UserId == DeriveUserId(record.Oid);

    /// <summary>Clears the identity cookie and removes the on-disk file. Called
    /// from /auth/logout.</summary>
    public void Clear(HttpContext ctx, string? oid)
    {
        var browserIdentity = oid is null ? Load(ctx) : null;
        ctx.Response.Cookies.Delete(IdentityCookieName);
        var targetOid = oid ?? browserIdentity?.Oid;
        if (!string.IsNullOrEmpty(targetOid))
        {
            var gate = LockFor(targetOid);
            gate.Wait();
            try
            {
                var path = Path.Combine(GetUserDir(targetOid), "identity.json");
                if (oid is not null) File.Delete(path);
                else
                {
                    var record = ReadRecord(targetOid, useCache: false);
                    if (record is not null)
                    {
                        record.BrowserVersion = Guid.NewGuid().ToString("N");
                        AtomicWrite(path, _protector.Protect(JsonSerializer.Serialize(record)));
                    }
                }
                _recordCache.TryRemove(targetOid, out _);
            }
            finally { gate.Release(); }
        }
    }

    /// <summary>Loads an identity by its derived userId, used by background
    /// services that have no HttpContext (e.g. <c>TenantTokenRefresher</c>).
    /// First-call after a process restart falls back to a cheap directory scan
    /// to populate the cache; subsequent calls are O(1).</summary>
    public IdentityRecord? LoadByUserId(long userId)
    {
        if (_userIdToOid.TryGetValue(userId, out var cachedOid))
            return LoadByOid(cachedOid);

        // Cold path after restart: walk users/ until we find a match. Cheap —
        // O(active users) and only on cache misses.
        var root = Path.Combine(_root, "users");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var oid = Path.GetFileName(dir);
            var rec = LoadByOid(oid);
            if (rec is not null && rec.UserId == userId) return rec;
        }
        return null;
    }

    /// <summary>Loads an identity by Entra OID directly (no cookie / context
    /// required). Returns null if the file is missing or undecryptable.</summary>
    public IdentityRecord? LoadByOid(string oid) => ReadRecord(oid, useCache: true);

    private IdentityRecord? ReadRecord(string oid, bool useCache)
    {
        if (!Guid.TryParseExact(oid, "D", out _)) return null;
        var now = _clock.GetUtcNow();
        if (useCache && _recordCache.TryGetValue(oid, out var cached) && cached.Expires > now)
            return Deserialize(cached.Json);
        var path = Path.Combine(GetUserDir(oid), "identity.json");
        if (!File.Exists(path))
        {
            _recordCache.TryRemove(oid, out _);
            return null;
        }
        try
        {
            var json = _protector.Unprotect(File.ReadAllText(path));
            if (_recordCache.Count > 1000)
                foreach (var stale in _recordCache.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
                    _recordCache.TryRemove(stale, out _);
            _recordCache[oid] = (json, now.Add(RecordCacheTtl));
            return Deserialize(json);
        }
        catch
        {
            return null;
        }
    }

    private static IdentityRecord? Deserialize(string json)
    {
        var record = JsonSerializer.Deserialize<IdentityRecord>(json);
        if (record is not null) _userIdToOid[record.UserId] = record.Oid;
        return record;
    }

    /// <summary>Updates only the refresh token + recorded scopes on an existing
    /// identity file. Used by <see cref="SessionTokenStore"/> when a refresh
    /// rotates the token (Entra rotates refresh tokens on use).</summary>
    public Task UpdateRefreshTokenAsync(string oid, string newRefreshToken)
    {
        return UpdateRecordAsync(oid, r => { r.RefreshToken = newRefreshToken; });
    }

    /// <summary>Persists the comma-separated list of consented Graph tiers so a
    /// post-restart hydration restores the user's full add-on set, not just the
    /// base ARM scope.</summary>
    public Task UpdateGraphTierAsync(string oid, string? graphTier)
    {
        return UpdateRecordAsync(oid, r => { r.GraphTier = graphTier; });
    }

    private async Task UpdateRecordAsync(string oid, Action<IdentityRecord> mutate)
    {
        var path = Path.Combine(GetUserDir(oid), "identity.json");
        if (!File.Exists(path)) return;
        var sem = LockFor(oid);
        await sem.WaitAsync();
        try
        {
            var existing = ReadRecord(oid, useCache: false);
            if (existing is null) return;
            mutate(existing);
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            AtomicWrite(path, _protector.Protect(JsonSerializer.Serialize(existing)));
            _recordCache.TryRemove(oid, out _);
            _userIdToOid[existing.UserId] = existing.Oid;
        }
        catch (CryptographicException ex)
        {
            _logger.LogInformation(
                "Identity record for oid={Oid} unreadable (key rotated); skipping update: {Reason}",
                oid, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update identity record for oid={Oid}", oid);
        }
        finally { sem.Release(); }
    }

    /// <summary>Crash-safe write: stage to a sibling .tmp then atomically replace
    /// the target. A torn write can leave the .tmp behind but never corrupts the
    /// live identity.json &#8212; users keep their refresh token across restarts.
    /// Retries briefly because /home is a network mount and a transient failure
    /// would otherwise fail the sign-in outright.</summary>
    private static void AtomicWrite(string path, string contents)
    {
        var tmp = path + ".tmp";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, contents);
                File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private string GetUserDir(string oid) => Guid.TryParseExact(oid, "D", out _)
        ? Path.Combine(_root, "users", oid) : throw new InvalidOperationException("Invalid identity.");
}

/// <summary>Encrypted-on-disk identity record. Contains only the long-lived
/// refresh token; access tokens are NOT persisted (they live ~1 hour anyway and
/// staying in-memory limits exposure).</summary>
public sealed class IdentityRecord
{
    public string Oid { get; set; } = "";
    public string TenantId { get; set; } = "";
    public long UserId { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? RefreshToken { get; set; }
    public string BrowserVersion { get; set; } = "";
    /// <summary>Comma-separated list of consented Graph tiers (e.g. "licenses,chargeback").</summary>
    public string? GraphTier { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
