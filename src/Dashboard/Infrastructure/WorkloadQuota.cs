using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace AzureFinOps.Dashboard.Infrastructure;

public sealed class WorkloadOptions
{
    public int MaxConcurrentRequests { get; init; } = 16;
    public int MaxConcurrentUploads { get; init; } = 2;
    public int MaxConcurrentTurns { get; init; } = 8;
    public int MaxConcurrentTurnsPerUser { get; init; } = 2;
    public int MaxConcurrentAnonymousTurns { get; init; } = 2;
    public int GlobalTurnsPerHour { get; init; } = 200;
    public int GlobalAnonymousTurnsPerHour { get; init; } = 120;
    public int AuthenticatedTurnsPerHour { get; init; } = 60;
    public int AnonymousTurnsPerHour { get; init; } = 5;
    public int GlobalRequestsPerHour { get; init; } = 600;
    public int GlobalAnonymousRequestsPerHour { get; init; } = 400;
    public int AuthenticatedRequestsPerHour { get; init; } = 120;
    public int AnonymousRequestsPerHour { get; init; } = 20;
    public int MaxPromptCharacters { get; init; } = 16_000;
    public int AnonymousMaxPromptCharacters { get; init; } = 2_000;
    public int MaxUploadMegabytes { get; init; } = 100;
    public int AnonymousMaxUploadMegabytes { get; init; } = 10;
    public int UploadedMegabytesPerUser { get; init; } = 300;
    public int AnonymousUploadedMegabytesPerUser { get; init; } = 20;
    public int GlobalUploadedMegabytes { get; init; } = 1024;
    public int GlobalUploadedFiles { get; init; } = 200;
    public int UploadedFilesPerUser { get; init; } = 20;
    public int AnonymousUploadedFilesPerUser { get; init; } = 3;
}

public sealed class WorkloadQuota
{
    internal static WorkloadQuota Default { get; } = new(new WorkloadOptions());
    public WorkloadOptions Options { get; }
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Window> _windows = new();
    private readonly Dictionary<string, int> _activeByCaller = new();
    private readonly Dictionary<long, (long Bytes, int Files)> _uploadsByUser = new();
    private int _activeTurns;
    private int _activeAnonymousTurns;
    private int _activeRequests;
    private int _activeUploads;
    private long _uploadedBytes;
    private int _uploadedFiles;

    public WorkloadQuota(WorkloadOptions options) : this(options, TimeProvider.System) { }

    internal WorkloadQuota(WorkloadOptions options, TimeProvider clock)
    {
        if (typeof(WorkloadOptions).GetProperties().Any(property => (int)property.GetValue(options)! <= 0))
            throw new ArgumentOutOfRangeException(nameof(options), "Workload limits must be positive.");
        Options = options;
        _clock = clock;
    }

    public async Task GuardRequestAsync(HttpContext context, Func<Task> next)
    {
        var path = (context.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
        var startsWork = context.Request.Method == "POST"
            && (path is "/api/chat" or "/api/chat/reset" or "/api/chat/warmup" or "/api/sessions/new" or "/api/upload" or "/api/jobs"
                || path.StartsWith("/api/jobs/", StringComparison.Ordinal) && path.EndsWith("/run", StringComparison.Ordinal));
        if (!startsWork) { await next(); return; }
        var authenticated = context.Session.GetString("browser_authenticated") == "1";
        var userJson = context.Session.GetString("user");
        if (userJson is null) { context.Response.StatusCode = 401; return; }
        var userId = JsonSerializer.Deserialize<JsonElement>(userJson).GetProperty("id").GetInt64();
        if (!TryAdmitRequest(userId, authenticated, out var retryAfterSeconds))
        {
            await RejectAsync(context, retryAfterSeconds);
            return;
        }
        using var requestLease = TryStartRequest(path == "/api/upload");
        if (requestLease is null)
        {
            await RejectAsync(context, 5);
            return;
        }
        var maxBytes = path == "/api/upload"
            ? ((long)(authenticated ? Options.MaxUploadMegabytes : Options.AnonymousMaxUploadMegabytes) + 2) * 1024 * 1024
            : 128L * 1024;
        if (context.Request.ContentLength > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "Request exceeds the workload size limit." });
            return;
        }
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = maxBytes;
        await next();
    }

    internal IDisposable? TryStartRequest(bool upload)
    {
        lock (_gate)
        {
            if (_activeRequests >= Options.MaxConcurrentRequests || upload && _activeUploads >= Options.MaxConcurrentUploads)
                return null;
            _activeRequests++;
            if (upload) _activeUploads++;
            return new Lease(() =>
            {
                lock (_gate)
                {
                    _activeRequests--;
                    if (upload) _activeUploads--;
                }
            });
        }
    }

    /// <summary>Anonymous callers are bucketed per browser session, so one visitor cannot
    /// exhaust the demo for everyone; clearing cookies earns a new bucket but still spends
    /// the global anonymous ceiling.</summary>
    public bool TryAdmitRequest(long userId, bool authenticated, out int retryAfterSeconds)
    {
        (string Key, int Limit)[] limits = authenticated
            ? [("requests:global", Options.GlobalRequestsPerHour), ($"requests:user:{userId}", Options.AuthenticatedRequestsPerHour)]
            : [("requests:global", Options.GlobalRequestsPerHour), ("requests:anonymous", Options.GlobalAnonymousRequestsPerHour),
               ($"requests:anonymous:{userId}", Options.AnonymousRequestsPerHour)];
        lock (_gate) return TryConsume(limits, out retryAfterSeconds);
    }

    public IDisposable? TryStartTurn(long userId, bool authenticated, out int retryAfterSeconds)
    {
        var caller = $"{(authenticated ? "user" : "anonymous")}:{userId}";
        (string Key, int Limit)[] limits = authenticated
            ? [("turns:global", Options.GlobalTurnsPerHour), ($"turns:{caller}", Options.AuthenticatedTurnsPerHour)]
            : [("turns:global", Options.GlobalTurnsPerHour), ("turns:anonymous", Options.GlobalAnonymousTurnsPerHour),
               ($"turns:{caller}", Options.AnonymousTurnsPerHour)];
        lock (_gate)
        {
            retryAfterSeconds = 5;
            var active = _activeByCaller.GetValueOrDefault(caller);
            if (_activeTurns >= Options.MaxConcurrentTurns
                || active >= (authenticated ? Options.MaxConcurrentTurnsPerUser : 1)
                || !authenticated && _activeAnonymousTurns >= Options.MaxConcurrentAnonymousTurns)
                return null;
            if (!TryConsume(limits, out retryAfterSeconds))
                return null;
            _activeTurns++;
            if (!authenticated) _activeAnonymousTurns++;
            _activeByCaller[caller] = active + 1;
            return new Lease(() =>
            {
                lock (_gate)
                {
                    _activeTurns--;
                    if (!authenticated) _activeAnonymousTurns--;
                    if (--_activeByCaller[caller] == 0) _activeByCaller.Remove(caller);
                }
            });
        }
    }

    public IDisposable? TryReserveUpload(long userId, bool authenticated, long bytes)
    {
        var maxFileBytes = (long)(authenticated ? Options.MaxUploadMegabytes : Options.AnonymousMaxUploadMegabytes) * 1024 * 1024;
        var maxUserBytes = (long)(authenticated ? Options.UploadedMegabytesPerUser : Options.AnonymousUploadedMegabytesPerUser) * 1024 * 1024;
        var maxFiles = authenticated ? Options.UploadedFilesPerUser : Options.AnonymousUploadedFilesPerUser;
        lock (_gate)
        {
            var usage = _uploadsByUser.GetValueOrDefault(userId);
            if (bytes <= 0 || bytes > maxFileBytes || usage.Files >= maxFiles
                || _uploadedFiles >= Options.GlobalUploadedFiles
                || bytes > maxUserBytes - usage.Bytes
                || bytes > (long)Options.GlobalUploadedMegabytes * 1024 * 1024 - _uploadedBytes)
                return null;
            _uploadedBytes += bytes;
            _uploadedFiles++;
            _uploadsByUser[userId] = (usage.Bytes + bytes, usage.Files + 1);
            return new Lease(() =>
            {
                lock (_gate)
                {
                    var current = _uploadsByUser[userId];
                    _uploadedBytes -= bytes;
                    _uploadedFiles--;
                    if (current.Files == 1) _uploadsByUser.Remove(userId);
                    else _uploadsByUser[userId] = (current.Bytes - bytes, current.Files - 1);
                }
            });
        }
    }

    public static Task RejectAsync(HttpContext context, int retryAfterSeconds)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = Math.Max(1, retryAfterSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return context.Response.WriteAsJsonAsync(new
        {
            error = "Workload limit reached. Wait before trying again.", code = "workload_limit", retryAfterSeconds = Math.Max(1, retryAfterSeconds)
        });
    }

    private bool TryConsume((string Key, int Limit)[] limits, out int retryAfterSeconds)
    {
        var now = _clock.GetUtcNow();
        if (_windows.Count > 1000)
            foreach (var key in _windows.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                _windows.Remove(key);
        retryAfterSeconds = 0;
        foreach (var (key, limit) in limits)
        {
            if (_windows.TryGetValue(key, out var window) && window.Expires > now && window.Count >= limit)
                retryAfterSeconds = Math.Max(retryAfterSeconds, (int)Math.Ceiling((window.Expires - now).TotalSeconds));
            if (!_windows.ContainsKey(key) && _windows.Count >= 10_000) retryAfterSeconds = Math.Max(retryAfterSeconds, 3600);
        }
        if (retryAfterSeconds > 0) return false;
        foreach (var (key, _) in limits)
        {
            if (!_windows.TryGetValue(key, out var window) || window.Expires <= now)
                _windows[key] = new Window(now.AddHours(1), 1);
            else _windows[key] = window with { Count = window.Count + 1 };
        }
        return true;
    }

    private sealed record Window(DateTimeOffset Expires, int Count);
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}