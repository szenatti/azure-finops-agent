using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzureFinOps.Dashboard.Infrastructure;

// Process-local coordination only; callers must hold Gate while reading or updating timing state.
internal sealed class CostQueryCoordinator(TimeProvider clock)
{
    private static readonly ConcurrentDictionary<string, CostQueryCoordinator> Tenants = new();
    private DateTimeOffset _retryAt;
    private DateTimeOffset _nextRequestAt;
    private readonly Dictionary<string, DateTimeOffset> _callerCooldowns = new();

    internal SemaphoreSlim Gate { get; } = new(1, 1);
    internal double RetryAfterSeconds => Math.Max(0, (_retryAt - clock.GetUtcNow()).TotalSeconds);
    internal TimeSpan PacingDelay => _nextRequestAt > clock.GetUtcNow()
        ? _nextRequestAt - clock.GetUtcNow() : TimeSpan.Zero;

    internal void RecordRequest() => _nextRequestAt = clock.GetUtcNow().AddSeconds(1);

    internal double GetRetryAfterSeconds(string token) => Math.Max(RetryAfterSeconds,
        _callerCooldowns.TryGetValue(CallerKey(token), out var retryAt) ? Math.Max(0, (retryAt - clock.GetUtcNow()).TotalSeconds) : 0);

    internal void RecordThrottle(double seconds, string? callerToken = null)
    {
        var now = clock.GetUtcNow();
        var availableSeconds = (DateTimeOffset.MaxValue - now).TotalSeconds;
        var retryAt = seconds >= availableSeconds ? DateTimeOffset.MaxValue : now.AddSeconds(seconds);
        if (callerToken is null)
        {
            if (retryAt > _retryAt) _retryAt = retryAt;
            return;
        }
        foreach (var key in _callerCooldowns.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            _callerCooldowns.Remove(key);
        var callerKey = CallerKey(callerToken);
        if (!_callerCooldowns.TryGetValue(callerKey, out var current) || retryAt > current)
            _callerCooldowns[callerKey] = retryAt;
    }

    private static string CallerKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static CostQueryCoordinator ForToken(string token) =>
        Tenants.GetOrAdd(TenantKey(token), _ => new CostQueryCoordinator(TimeProvider.System));

    // Unverified claims select a throttle bucket, never authorize requests. ARM validates the token.
    private static string TenantKey(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return "unknown";
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.TryGetProperty("tid", out var tenant)
                && Guid.TryParse(tenant.GetString(), out var tenantId))
                return tenantId.ToString();
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException)
        {
        }
        return "unknown";
    }
}