using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace AzureFinOps.Dashboard.Infrastructure;

internal sealed record AzureScope(string Id, string Name, string? State, string? TenantId);
internal sealed record ScopeDiscoveryResult(IReadOnlyList<AzureScope> Scopes, bool Complete, string? Error);

internal static class AzureScopeDiscovery
{
    private const string SubscriptionPath = "/subscriptions";
    private const string ManagementGroupPath = "/providers/Microsoft.Management/managementGroups";
    private static readonly HttpClient Http = CreateClient();
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 128 });
    private static readonly object CacheLock = new();

    internal static Task<ScopeDiscoveryResult> SubscriptionsAsync(string token) => GetAsync(token, false);
    internal static Task<ScopeDiscoveryResult> ManagementGroupsAsync(string token) => GetAsync(token, true);

    private static HttpClient CreateClient()
    {
        var handler = Ipv4HttpHandler.Create();
        handler.AllowAutoRedirect = false;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal static async Task<ScopeDiscoveryResult> GetAsync(string token, bool managementGroups, HttpClient? http = null)
    {
        // Cache by caller token, not tenant: users in the same tenant can see different subscriptions.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) + ":" + managementGroups;
        Lazy<Task<ScopeDiscoveryResult>> pending;
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out pending!))
            {
                // Coalesce simultaneous status/tool discovery without holding the lock across HTTP calls.
                pending = new(() => ReadPagesAsync(http ?? Http, token, managementGroups));
                Cache.Set(key, pending, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                });
            }
        }
        var result = await pending.Value;
        if (!result.Complete)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out object? current) && ReferenceEquals(current, pending))
                    Cache.Remove(key);
            }
        }
        return result;
    }

    internal static async Task<ScopeDiscoveryResult> ReadPagesAsync(HttpClient http, string token, bool managementGroups)
    {
        var path = managementGroups ? ManagementGroupPath : SubscriptionPath;
        var version = managementGroups ? "2021-04-01" : "2022-12-01";
        var next = new Uri($"https://management.azure.com{path}?api-version={version}");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new Dictionary<string, AzureScope>(StringComparer.OrdinalIgnoreCase);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                if (!IsSafeContinuation(next, path) || !visited.Add(next.AbsoluteUri) || visited.Count > 100)
                    return new(scopes.Values.ToArray(), false, "Discovery stopped at an invalid or repeated continuation.");
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.UserAgent.ParseAdd("FinOps-Dashboard/1.0");
                using var response = await http.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return new(scopes.Values.ToArray(), false, $"Scope discovery returned HTTP {(int)response.StatusCode}.");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                    return new(scopes.Values.ToArray(), false, "Scope discovery returned an invalid page.");
                foreach (var item in values.EnumerateArray())
                {
                    var id = item.GetProperty(managementGroups ? "id" : "subscriptionId").GetString();
                    var name = managementGroups
                        ? item.TryGetProperty("properties", out var properties) && properties.TryGetProperty("displayName", out var displayName)
                            ? displayName.GetString() : item.GetProperty("name").GetString()
                        : item.GetProperty("displayName").GetString();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                        return new(scopes.Values.ToArray(), false, "Scope discovery returned an invalid scope.");
                    if (!managementGroups)
                    {
                        if (!Guid.TryParse(id, out var subscriptionId))
                            return new(scopes.Values.ToArray(), false, "Scope discovery returned an invalid subscription.");
                        id = subscriptionId.ToString();
                    }
                    scopes[id] = new(id, name,
                        item.TryGetProperty("state", out var state) ? state.GetString() : null,
                        item.TryGetProperty("tenantId", out var tenant) ? tenant.GetString() : null);
                    if (scopes.Count > 10000)
                        return new(scopes.Values.Take(10000).ToArray(), false, "Discovery exceeded 10000 scopes; narrow the account access scope.");
                }
                var link = document.RootElement.TryGetProperty("nextLink", out var nextLink) ? nextLink.GetString() : null;
                if (string.IsNullOrWhiteSpace(link)) return new(scopes.Values.ToArray(), true, null);
                if (!Uri.TryCreate(next, link, out var continuation))
                    return new(scopes.Values.ToArray(), false, "Scope discovery returned an invalid continuation.");
                next = continuation;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new(scopes.Values.ToArray(), false, "Scope discovery failed; retry later. Results are incomplete.");
        }
    }

    // Validate each continuation before attaching the bearer token; redirects are disabled on Http.
    internal static bool IsSafeContinuation(Uri uri, string path) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && uri.AbsolutePath.TrimEnd('/').Equals(path, StringComparison.OrdinalIgnoreCase);

    internal static string Find(ScopeDiscoveryResult discovery, string query, int offset)
    {
        var search = query.Trim();
        var matches = discovery.Scopes.Where(scope => scope.Id.Equals(search, StringComparison.OrdinalIgnoreCase)
            || scope.Name.Equals(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            matches = discovery.Scopes.Where(scope => scope.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || scope.Id.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var page = matches.OrderBy(scope => scope.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(scope => scope.Id).Skip(offset).Take(50).ToArray();
        return JsonSerializer.Serialize(new
        {
            complete = discovery.Complete,
            error = discovery.Error,
            subscriptionCount = discovery.Scopes.Count,
            matchCount = matches.Length,
            nextOffset = offset + page.Length < matches.Length ? offset + page.Length : (int?)null,
            subscriptions = page.Select(scope => new { id = scope.Id, name = scope.Name, state = scope.State })
        });
    }
}