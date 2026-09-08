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
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-entity-retry-after", "31");
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-qpu-remaining", new string('0', 1000));
    response.Headers.TryAddWithoutValidation("Set-Cookie", "synthetic-cookie=never-export");
    response.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic-never-export");
    response.Headers.TryAddWithoutValidation("x-ms-unrelated-header", "never-export");
    return response;
}));
var throttledToken = Token(Guid.NewGuid(), "throttled");
const string costUrl = "https://management.azure.com/subscriptions/test/providers/Microsoft.CostManagement/query";
var throttledResult = await HttpHelper.SendCoreAsync(throttledHttp, costUrl, throttledToken, null, "test", HttpMethod.Post);
Check(throttledCalls == 1 && throttledResult.Contains("Retry-After: 120"), "Long cooldown returns immediately without early retry");
var subsequent = await HttpHelper.SendCoreAsync(throttledHttp, costUrl, throttledToken, null, "test", HttpMethod.Post);
Check(throttledCalls == 1 && subsequent.Contains("TenantCostCooldown"), "Real 429 blocks subsequent turns");
using (var response = JsonDocument.Parse(throttledResult[(throttledResult.IndexOf('\n') + 1)..]))
{
    var retry = response.RootElement.GetProperty("finopsRetry");
    Check(retry.GetProperty("retryAfterSeconds").GetDouble() == 120 && retry.GetProperty("source").GetString() == "azure"
        && retry.GetProperty("delaySource").GetString() == "server", "Server cooldown is visible in JSON-rendered tool output");
    var rateHeaders = retry.GetProperty("rateLimitHeaders");
    Check(rateHeaders.GetProperty("x-ms-ratelimit-microsoft.consumption-retry-after").GetString() == "120"
        && rateHeaders.GetProperty("x-ms-ratelimit-microsoft.costmanagement-entity-retry-after").GetString() == "31",
        "Throttle result identifies the contributing rate-limit headers without changing the longest delay");
    Check(rateHeaders.GetProperty("x-ms-ratelimit-microsoft.costmanagement-qpu-remaining").GetString()!.Length == 256,
        "Rate-limit diagnostics have bounded values");
    Check(rateHeaders.EnumerateObject().Count() == 3 && !throttledResult.Contains("never-export"),
        "Rate-limit diagnostics exclude credentials, cookies and unrelated headers");
}
using (var response = JsonDocument.Parse(subsequent[(subsequent.IndexOf('\n') + 1)..]))
{
    Check(response.RootElement.GetProperty("finopsRetry").GetProperty("source").GetString() == "localCooldown",
        "Execution output distinguishes a local cooldown from an Azure rejection");
    Check(!response.RootElement.GetProperty("finopsRetry").GetProperty("rateLimitHeaders").EnumerateObject().Any(),
        "Local cooldown does not invent Azure response headers");
}
var headerlessCalls = 0;
using (var headerlessHttp = new HttpClient(new StubHandler(_ =>
{
    headerlessCalls++;
    return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"429\"}}") };
})))
{
    var response = await HttpHelper.SendCoreAsync(headerlessHttp, costUrl, Token(Guid.NewGuid(), "no-header"), null, "test", HttpMethod.Post);
    using var json = JsonDocument.Parse(response[(response.IndexOf('\n') + 1)..]);
    var retry = json.RootElement.GetProperty("finopsRetry");
    Check(headerlessCalls == 1 && retry.GetProperty("retryAfterSeconds").GetDouble() == 60
        && retry.GetProperty("delaySource").GetString() == "fallback", "Headerless 429 uses an explicit conservative fallback without a rapid retry");
}

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
var crawlScopes = Enumerable.Range(0, 227).Select(index => new CrawlMaturityTools.SubscriptionScope(
    Guid.NewGuid().ToString(), "Example-subscription-" + index + new string('x', 150))).ToArray();
var crawlBudgets = crawlScopes.Select(scope => new CrawlMaturityTools.BudgetEvidence(
    scope.Id, scope.Name, 200, 1, 100, 12, "AUD", 1, 1, null)).ToArray();
var crawlCollections = crawlScopes.Select(scope => new CrawlMaturityTools.CollectionEvidence(
    scope.Id, scope.Name, 200, 8, Enumerable.Repeat(new string('y', 250), 10).ToArray(), null)).ToArray();
var crawlTags = new { status = 200, data = crawlScopes.Select(scope => new
    { subscriptionId = scope.Id, total = 200, fullyTagged = 1, costCenter = 1, owner = 1, environment = 1 }).ToArray() };
var crawlPolicy = new { status = 200, data = crawlScopes.Select(scope => new
    { subscriptionId = scope.Id, totalAssignments = 5, finOpsAssignments = 1 }).ToArray() };
var crawlWaste = new { status = 200, data = crawlScopes.Select(scope => new
    { subscriptionId = scope.Id, wasteCount = 2, names = Enumerable.Repeat(new string('z', 250), 10).ToArray() }).ToArray() };
var crawlEmpty = new { status = 200, data = crawlScopes.Select(scope => new
    { subscriptionId = scope.Id, emptyGroupCount = 3, names = Enumerable.Repeat(new string('z', 250), 10).ToArray() }).ToArray() };
var crawlTotals = new Dictionary<string, double> { ["AUD"] = 227 * 12 };
var crawlScores = CrawlMaturityTools.BuildScores(crawlScopes, crawlBudgets, crawlTags, crawlCollections,
    crawlCollections, crawlCollections, crawlPolicy, crawlWaste, crawlEmpty, 227 * 12, "AUD", crawlTotals);
var crawlEvidence = new
{
    generatedUtc = "2026-09-08T00:00:00Z", subscriptionCount = 227, subscriptions = crawlScopes,
    budgets = new { totalBudgets = 227, spendComplete = true, totalsByCurrency = crawlTotals, details = crawlBudgets },
    visibility = new { subscriptionsWithValidatedSpend = 227, totalsByCurrency = crawlTotals },
    tagging = crawlTags, policy = crawlPolicy, exports = crawlCollections, scheduledActions = crawlCollections,
    alerts = crawlCollections, waste = new { commonPatterns = crawlWaste, emptyResourceGroups = crawlEmpty }
};
var compactCrawl = JsonSerializer.Serialize(new { kind = "crawl_maturity_result", scores = crawlScores,
    followUp = new { label = "Review evidence", prompt = "Review evidence" },
    evidence = CrawlMaturityTools.SummarizeEvidence(crawlEvidence) });
Check(Encoding.UTF8.GetByteCount(compactCrawl) < 24000, "227-subscription Crawl result stays below 24KB");
Check(crawlScores.Count == 7 && crawlScores.All(score => score.Detail.Length < 1000), "All seven Crawl score explanations stay bounded");
using (var compactDoc = JsonDocument.Parse(compactCrawl))
{
    var evidence = compactDoc.RootElement.GetProperty("evidence");
    Check(evidence.GetProperty("tagging").GetProperty("totals").GetProperty("total").GetDouble() == 45400,
        "Compact Crawl preserves estate-wide numeric evidence");
    Check(evidence.GetProperty("exports").GetProperty("itemCount").GetInt32() == 1816,
        "Compact Crawl preserves full collection counts");
    Check(evidence.GetProperty("waste").GetProperty("commonPatterns").GetProperty("samples").GetArrayLength() == 3,
        "Crawl resource samples are bounded");
}
Check(crawlScores.First(score => score.Id == "budgets").Detail.Contains("Last evaluated")
    && !crawlScores.Any(score => score.Detail.Contains("MTD")), "Crawl score prose never labels budget evaluations as MTD cost");
var crawlTenant = Guid.NewGuid();
var crawlToken = Token(crawlTenant, "crawl-user");
var crawlCalls = 0;
using var crawlHttp = new HttpClient(new StubHandler(request =>
{
    crawlCalls++;
    return request.Method == HttpMethod.Post ? JsonResponse(new { data = Array.Empty<object>() })
        : JsonResponse(new { value = Array.Empty<object>() });
}));
var savedCrawl = "";
var crawlTool = new CrawlMaturityTools(new UserTokens { AzureToken = crawlToken },
    (_, scores) => savedCrawl = scores, crawlHttp, TimeSpan.FromSeconds(30)).Create().Single();
var crawlArguments = new AIFunctionArguments { ["subscriptionsJson"] = JsonSerializer.Serialize(
    crawlScopes.Select(scope => new { id = scope.Id, name = scope.Name })) };
var completeCrawl = await Invoke(crawlTool, crawlArguments);
using (var result = JsonDocument.Parse(completeCrawl))
{
    Check(result.RootElement.GetProperty("complete").GetBoolean() && crawlCalls == 912,
        "Crawl collects four categories and four Resource Graph projections for 227 scopes");
    Check(result.RootElement.GetProperty("scores").GetArrayLength() == 7 && savedCrawl.Contains("evidenceComplete"),
        "Crawl persists all seven scores with evidence completeness");
    Check(Encoding.UTF8.GetByteCount(completeCrawl) < 24000, "Actual Crawl tool response stays within 24KB");
    Console.WriteLine($"Crawl fixture response size: {Encoding.UTF8.GetByteCount(completeCrawl)} bytes");
}
await Invoke(crawlTool, crawlArguments);
Check(crawlCalls == 912, "Repeated Crawl reuses caller-scoped evidence without more API calls");
var anotherCrawl = new CrawlMaturityTools(new UserTokens { AzureToken = Token(crawlTenant, "other-user") },
    (_, _) => { }, crawlHttp, TimeSpan.FromSeconds(30)).Create().Single();
await Invoke(anotherCrawl, crawlArguments);
Check(crawlCalls == 1824, "Crawl evidence is not shared between users in the same tenant");
var allCrawl = new CrawlMaturityTools(new UserTokens { AzureToken = toolToken },
    (_, _) => { }, crawlHttp, TimeSpan.FromSeconds(30)).Create().Single();
using (var result = JsonDocument.Parse(await Invoke(allCrawl, new AIFunctionArguments { ["subscriptionsJson"] = "all" })))
    Check(result.RootElement.GetProperty("evidence").GetProperty("subscriptionCount").GetInt32() == 230,
        "Crawl all uses host discovery instead of a model-copied scope array");
using (var truncatedHttp = new HttpClient(new StubHandler(request => request.Method == HttpMethod.Post
    ? JsonResponse(new { data = Array.Empty<object>(), resultTruncated = "true" })
    : JsonResponse(new { value = Array.Empty<object>() }))))
{
    var truncatedTool = new CrawlMaturityTools(new UserTokens { AzureToken = Token(Guid.NewGuid(), "truncated") },
        (_, _) => { }, truncatedHttp, TimeSpan.FromSeconds(30)).Create().Single();
    using var result = JsonDocument.Parse(await Invoke(truncatedTool, new AIFunctionArguments
    {
        ["subscriptionsJson"] = JsonSerializer.Serialize(new[] { new { id = Guid.NewGuid().ToString(), name = "Example" } })
    }));
    Check(!result.RootElement.GetProperty("complete").GetBoolean()
        && result.RootElement.GetProperty("evidence").GetProperty("tagging").GetProperty("status").GetInt32() == 206,
        "Truncated Resource Graph data cannot produce complete Crawl evidence");
}
var slowHandler = new CancelOnRequestHandler();
using var slowHttp = new HttpClient(slowHandler);
var partialTool = new CrawlMaturityTools(new UserTokens { AzureToken = Token(Guid.NewGuid(), "slow-crawl") },
    (_, _) => { }, slowHttp, TimeSpan.FromMilliseconds(100)).Create().Single();
var partialTimer = System.Diagnostics.Stopwatch.StartNew();
var partialCrawl = await Invoke(partialTool, crawlArguments);
using (var result = JsonDocument.Parse(partialCrawl))
{
    Check(!result.RootElement.GetProperty("complete").GetBoolean()
        && result.RootElement.GetProperty("diagnostics").GetProperty("deadlineReached").GetBoolean(),
        "Crawl deadline returns an explicitly incomplete assessment");
    Check(result.RootElement.GetProperty("scores").EnumerateArray().All(score => !score.GetProperty("evidenceComplete").GetBoolean()),
        "Timeouts never appear as verified absence of controls");
}
Check(partialTimer.Elapsed < TimeSpan.FromSeconds(5) && slowHandler.Requests <= 12 && slowHandler.Active == 0,
    "Crawl cancels queued and in-flight work, preserving the concurrency bound");
using (var cancelled = new CancellationTokenSource())
using (var neverCalled = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Cancelled request reached HTTP"))))
{
    cancelled.Cancel();
    var cancelledCorrectly = false;
    try { await HttpHelper.SendCoreAsync(neverCalled, "https://management.azure.com/test", "test", null, "test", cancellationToken: cancelled.Token); }
    catch (OperationCanceledException) { cancelledCorrectly = true; }
    Check(cancelledCorrectly, "Cancelled evidence request does not reach HTTP");
}
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

sealed class CancelOnRequestHandler : HttpMessageHandler
{
    public int Requests;
    public int Active;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        Interlocked.Increment(ref Active);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Expected request cancellation");
        }
        finally { Interlocked.Decrement(ref Active); }
    }
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-08T00:00:00Z");
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}