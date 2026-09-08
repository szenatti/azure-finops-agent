using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

var retryMethod = typeof(HttpHelper).GetMethod("ResolveRetryAfterSeconds", BindingFlags.Static | BindingFlags.NonPublic)!;
var cases = new (string Name, string Header, string Value, double Expected)[]
{
    ("Consumption", "x-ms-ratelimit-microsoft.consumption-retry-after", "60", 60),
    ("Entity", "x-ms-ratelimit-microsoft.costmanagement-entity-retry-after", "60", 60),
    ("Tenant", "x-ms-ratelimit-microsoft.costmanagement-tenant-retry-after", "90", 90),
    ("Client", "x-ms-ratelimit-microsoft.costmanagement-clienttype-retry-after", "45", 45),
    ("Long QPU", "x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "120", 120),
    ("Standard", "Retry-After", "30", 30)
};
foreach (var test in cases)
{
    using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
    response.Headers.TryAddWithoutValidation(test.Header, test.Value);
    var actual = (double)retryMethod.Invoke(null, [response, 0])!;
    Check(actual == test.Expected, test.Name);
}
using (var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests))
{
    response.Headers.TryAddWithoutValidation("Retry-After", "60");
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-qpu-retry-after", "5");
    Check((double)retryMethod.Invoke(null, [response, 0])! == 60, "Longest retry header wins");
}

var clock = new TestClock();
var coordinator = new CostQueryCoordinator(clock);
coordinator.RecordThrottle(120);
coordinator.RecordThrottle(5);
Check(coordinator.RetryAfterSeconds == 120, "Shorter throttle cannot shorten cooldown");
clock.Advance(TimeSpan.FromSeconds(120));
Check(coordinator.RetryAfterSeconds == 0, "Cooldown expires");
coordinator.RecordRequest();
Check(coordinator.PacingDelay == TimeSpan.FromSeconds(1), "Requests are paced");
clock.Advance(TimeSpan.FromSeconds(1));
Check(coordinator.PacingDelay == TimeSpan.Zero, "Pacing expires");

var tenant = Guid.NewGuid();
var token = Token(tenant, "first");
var tenantState = CostQueryCoordinator.ForToken(token);
Check(ReferenceEquals(tenantState, CostQueryCoordinator.ForToken(Token(tenant, "second"))), "Tenant cooldown shared across tokens");
Check(!ReferenceEquals(tenantState, CostQueryCoordinator.ForToken(Token(Guid.NewGuid(), "third"))), "Tenants isolated");
tenantState.RecordThrottle(120);
var blocked = await HttpHelper.SendWithRetryAsync(
    "https://must-not-be-called.invalid/providers/Microsoft.CostManagement/query",
    token, null, "test", method: HttpMethod.Post);
Check(blocked.Contains("TenantCostCooldown"), "Cooldown prevents network request");

var subscriptionIds = Enumerable.Range(0, 230).Select(_ => Guid.NewGuid().ToString()).ToArray();
var pageCalls = 0;
using var discoveryHttp = new HttpClient(new StubHandler(request =>
{
    Check(request.Headers.Authorization?.Parameter == "test-token", "Discovery keeps caller token");
    var secondPage = ++pageCalls == 2;
    var entries = subscriptionIds.Skip(secondPage ? 200 : 0).Take(secondPage ? 30 : 200)
        .Select((id, index) => new { subscriptionId = id, displayName = secondPage && index == 29 ? "Target-Infrastructure" : "Subscription-" + id, state = "Enabled" });
    return JsonResponse(new { value = entries, nextLink = secondPage ? null : "https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=next" });
}));
var discovery = await AzureScopeDiscovery.ReadPagesAsync(discoveryHttp, "test-token", false);
Check(discovery.Complete && discovery.Scopes.Count == 230 && pageCalls == 2, "Discovery follows all subscription pages");
using (var match = JsonDocument.Parse(AzureScopeDiscovery.Find(discovery, "target-infrastructure", 0)))
{
    Check(match.RootElement.GetProperty("subscriptions")[0].GetProperty("id").GetString() == subscriptionIds[229], "Lookup resolves a name on the second page");
    Check(match.RootElement.GetProperty("matchCount").GetInt32() == 1, "Lookup returns only matching subscription");
}
using (var bounded = JsonDocument.Parse(AzureScopeDiscovery.Find(discovery, "", 0)))
    Check(bounded.RootElement.GetProperty("subscriptions").GetArrayLength() == 50
        && bounded.RootElement.GetProperty("nextOffset").GetInt32() == 50, "Lookup output is bounded and pageable");

foreach (var link in new[] { "https://evil.invalid/subscriptions", "http://management.azure.com/subscriptions",
    "https://management.azure.com:444/subscriptions", "https://management.azure.com/providers/Microsoft.Other/list" })
{
    var calls = 0;
    using var unsafeHttp = new HttpClient(new StubHandler(_ =>
    {
        calls++;
        return JsonResponse(new { value = Array.Empty<object>(), nextLink = link });
    }));
    var unsafeResult = await AzureScopeDiscovery.ReadPagesAsync(unsafeHttp, "test-token", false);
    Check(!unsafeResult.Complete && calls == 1, "Unsafe continuation blocked without forwarding token");
}
var failureCalls = 0;
using var failingHttp = new HttpClient(new StubHandler(_ => ++failureCalls == 1
    ? JsonResponse(new { value = new[] { new { subscriptionId = subscriptionIds[0], displayName = "First", state = "Enabled" } },
        nextLink = "https://management.azure.com/subscriptions?page=2" })
    : new HttpResponseMessage(HttpStatusCode.Forbidden)));
var partial = await AzureScopeDiscovery.ReadPagesAsync(failingHttp, "test-token", false);
Check(!partial.Complete && partial.Scopes.Count == 1, "Failed later page is explicitly incomplete");

var throttledCalls = 0;
using var throttledHttp = new HttpClient(new StubHandler(_ =>
{
    throttledCalls++;
    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"429\"}}") };
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.consumption-retry-after", "120");
    return response;
}));
var throttledToken = Token(Guid.NewGuid(), "throttled");
const string costUrl = "https://management.azure.com/subscriptions/test/providers/Microsoft.CostManagement/query";
var throttledResult = await HttpHelper.SendCoreAsync(throttledHttp, costUrl, throttledToken, null, "test", HttpMethod.Post);
Check(throttledCalls == 1 && throttledResult.Contains("Retry-After: 120"), "Long cooldown returns immediately without early retry");
var subsequent = await HttpHelper.SendCoreAsync(throttledHttp, costUrl, throttledToken, null, "test", HttpMethod.Post);
Check(throttledCalls == 1 && subsequent.Contains("TenantCostCooldown"), "Real 429 blocks subsequent turns");

var cachedCalls = 0;
using var costHttp = new HttpClient(new StubHandler(_ =>
{
    cachedCalls++;
    return JsonResponse(new { properties = new { rows = new[] { new object[] { 12.5, "USD" } } } });
}));
var cachedToken = Token(Guid.NewGuid(), "cached");
var original = await HttpHelper.SendCoreAsync(costHttp, costUrl, cachedToken, null, "test", HttpMethod.Post, "{}");
CostQueryCoordinator.ForToken(cachedToken).RecordThrottle(60);
var reused = await HttpHelper.SendCoreAsync(costHttp, costUrl, cachedToken, null, "test", HttpMethod.Post, "{}");
Check(cachedCalls == 1 && original == reused && reused.Contains("Cost data fetched"), "Identical successful query reused with freshness during cooldown");
using (var costBody = JsonDocument.Parse(reused[(reused.IndexOf('\n') + 1)..]))
    Check(costBody.RootElement.GetProperty("properties").GetProperty("rows").GetArrayLength() == 1,
        "Cost freshness preserves existing HTTP-plus-JSON parser contract");
var changed = await HttpHelper.SendCoreAsync(costHttp, costUrl, cachedToken, null, "test", HttpMethod.Post, "{\"different\":true}");
Check(changed.Contains("TenantCostCooldown"), "Different query cannot reuse cached cost");
await HttpHelper.SendCoreAsync(costHttp, costUrl, Token(Guid.NewGuid(), "other-user"), null, "test", HttpMethod.Post, "{}");
Check(cachedCalls == 2, "Cost cache isolated by caller token");

var toolToken = Token(Guid.NewGuid(), "tool-test");
var toolDiscoveryCalls = 0;
using var toolDiscoveryHttp = new HttpClient(new StubHandler(_ =>
{
    toolDiscoveryCalls++;
    return JsonResponse(new { value = subscriptionIds.Select((id, index) => new
    {
        subscriptionId = id, displayName = index == 229 ? "Target-Infrastructure" : "Subscription-" + index, state = "Enabled"
    }) });
}));
await AzureScopeDiscovery.GetAsync(toolToken, false, toolDiscoveryHttp);
var tools = new AzureQueryTools(new UserTokens { AzureToken = toolToken }).Create().ToDictionary(tool => tool.Name);
var lookup = await Invoke(tools["FindSubscriptions"], new AIFunctionArguments { ["search"] = "target-infrastructure" });
using (var match = JsonDocument.Parse(lookup))
    Check(match.RootElement.GetProperty("matchCount").GetInt32() == 1 && toolDiscoveryCalls == 1,
        "Chat lookup reuses the host discovery cache");
var listing = await Invoke(tools["QueryAzure"], new AIFunctionArguments
{
    ["method"] = "GET", ["path"] = "/subscriptions?api-version=2022-12-01"
});
using (var list = JsonDocument.Parse(listing))
    Check(list.RootElement.GetProperty("subscriptions").GetArrayLength() == 50 && toolDiscoveryCalls == 1,
        "Raw subscription list requests route to compact cached output");

CostQueryCoordinator.ForToken(toolToken).RecordThrottle(120);
var today = DateOnly.FromDateTime(DateTime.UtcNow);
var total = await Invoke(tools["QueryCostsAcrossSubscriptions"], new AIFunctionArguments
{
    ["subscriptionsJson"] = "all", ["from"] = new DateOnly(today.Year, today.Month, 1).ToString("yyyy-MM-dd"),
    ["to"] = today.AddDays(1).ToString("yyyy-MM-dd")
});
using (var summary = JsonDocument.Parse(total))
{
    var root = summary.RootElement;
    Check(root.GetProperty("subscriptionCount").GetInt32() == 230, "All-subscription tool uses full host scope list");
    Check(!root.GetProperty("complete").GetBoolean() && root.GetProperty("totalCost").ValueKind == JsonValueKind.Null,
        "Throttled coverage never reports a complete estate total");
    Check(root.GetProperty("unattempted").GetInt32() == 229 && root.GetProperty("results").GetArrayLength() == 50
        && root.GetProperty("resultsTruncated").GetBoolean(), "Large partial result preserves full coverage with bounded details");
}
Check(typeof(AzureQueryTools).GetMethod("TryReadCurrentMonthSpendFromBudgets", BindingFlags.NonPublic | BindingFlags.Instance) is null,
    "Live cost totals no longer use the budget fan-out shortcut");
var parseCost = typeof(AzureQueryTools).GetMethod("TryReadCost", BindingFlags.Static | BindingFlags.NonPublic)!;
object?[] parseArguments = ["HTTP 200 OK\n" + JsonSerializer.Serialize(new { properties = new { nextLink = "https://management.azure.com/next" } }), 0d, null, null];
Check(!(bool)parseCost.Invoke(null, parseArguments)!, "Unfinished cost page cannot become a total");
Console.WriteLine("All large-tenant regression checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
}

static async Task<string> Invoke(AIFunction tool, AIFunctionArguments arguments)
{
    var result = await tool.InvokeAsync(arguments);
    return result is JsonElement element ? element.GetString()! : (string)result!;
}

static string Token(Guid tenant, string subject) =>
    "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tid = tenant, sub = subject })))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";

static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
};

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}