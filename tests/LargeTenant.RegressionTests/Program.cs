using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AzureFinOps.Dashboard.AI.Tools;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;

var callbackOwner = Random.Shared.NextInt64();
Func<int, double, string, string, int, Task> noOpReporter = (_, _, _, _, _) => Task.CompletedTask;
for (var race = 0; race < 100; race++)
{
    var key = Guid.NewGuid().ToString();
    using var registration = new RetryReporterRegistration();
    Parallel.Invoke(() => registration.Bind(key, noOpReporter), registration.Dispose);
    registration.Bind(key, noOpReporter);
    if (HttpHelper.RetryReporters.ContainsKey(key)) throw new InvalidOperationException("Detached reporter leaked");
}
Check(true, "B11: Concurrent rebind/dispose cannot leak or resurrect a reporter");
using (var oldReporter = new RetryReporterRegistration())
using (var newReporter = new RetryReporterRegistration())
{
    var key = Guid.NewGuid().ToString();
    Func<int, double, string, string, int, Task> replacement = (_, _, _, _, _) => Task.FromResult(1);
    oldReporter.Bind(key, noOpReporter);
    newReporter.Bind(key, replacement);
    oldReporter.Dispose();
    Check(HttpHelper.RetryReporters[key] == replacement, "B11: Late disposal does not remove a replacement reporter");
}
var identityProbe = Guid.NewGuid().ToString();
var mismatchedDisposed = false;
await SessionBoundTool.VerifySessionIdAsync(identityProbe, identityProbe, () => throw new InvalidOperationException("Valid session disposed"));
var identityRejected = false;
try
{
    await SessionBoundTool.VerifySessionIdAsync(identityProbe, Guid.NewGuid().ToString(), () =>
    { mismatchedDisposed = true; return Task.CompletedTask; });
}
catch (InvalidOperationException) { identityRejected = true; }
Check(identityRejected && mismatchedDisposed, "B3: SDK session identity mismatch disposes the handle and fails before registration");
var callbackSession = Guid.NewGuid().ToString();
var parallelSession = Guid.NewGuid().ToString();
var jobSession = Guid.NewGuid().ToString();
var callbackKey = $"{callbackOwner}:{callbackSession}";
var parallelKey = $"{callbackOwner}:{parallelSession}";
var reportedCallbacks = new System.Collections.Concurrent.ConcurrentQueue<string>();
var callbackGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var callbackStarts = 0;
using var callbackHttp = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
{
    Content = new StringContent("{\"error\":{\"code\":\"429\"}}")
}));
var callbackInner = AIFunctionFactory.Create(async () =>
{
    if (Interlocked.Increment(ref callbackStarts) == 3) callbackGate.SetResult();
    await callbackGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(HttpHelper.CurrentTurnUserId() == callbackOwner, "Session-bound callback restores the host owner without ambient context");
    return await HttpHelper.SendCoreAsync(callbackHttp,
        "https://management.azure.com/subscriptions/test/providers/Microsoft.CostManagement/query",
        Token(Guid.NewGuid(), "callback"), null, "test", HttpMethod.Post);
}, "CallbackProbe", "Synthetic session context probe");
HttpHelper.RetryReporters[callbackKey] = (_, _, _, _, _) => { reportedCallbacks.Enqueue(callbackKey); return Task.CompletedTask; };
HttpHelper.RetryReporters[parallelKey] = (_, _, _, _, _) => { reportedCallbacks.Enqueue(parallelKey); return Task.CompletedTask; };
try
{
    var callbacks = new[]
    {
        new SessionBoundTool(callbackInner, callbackOwner, callbackSession),
        new SessionBoundTool(callbackInner, callbackOwner, parallelSession),
        new SessionBoundTool(callbackInner, callbackOwner, jobSession)
    };
    Task[] pending;
    using (ExecutionContext.SuppressFlow())
        pending = callbacks.Select(tool => Task.Run(async () =>
        {
            Check(System.Diagnostics.Activity.Current is null, "Synthetic SDK callback has no inherited activity");
            await tool.InvokeAsync(new AIFunctionArguments { ["sessionId"] = "untrusted-session", ["userId"] = "untrusted-owner" });
            Check(System.Diagnostics.Activity.Current is null, "Callback activity is cleaned up after execution");
        })).ToArray();
    await Task.WhenAll(pending);
    Check(reportedCallbacks.Count == 2 && reportedCallbacks.Count(key => key == callbackKey) == 1
        && reportedCallbacks.Count(key => key == parallelKey) == 1,
        "Cooldown callbacks reach only their own session; background jobs do not borrow another chat reporter");
    var deferredCallback = DeferredTool.Wrap(callbackInner);
    var boundDeferred = new SessionBoundTool(deferredCallback, callbackOwner, callbackSession);
    Check(boundDeferred.Name == deferredCallback.Name && boundDeferred.JsonSchema.GetRawText() == deferredCallback.JsonSchema.GetRawText()
        && Equals(boundDeferred.AdditionalProperties["defer"], deferredCallback.AdditionalProperties["defer"]),
        "Session binding preserves tool schemas and deferred-tool metadata");
}
finally
{
    HttpHelper.RetryReporters.TryRemove(callbackKey, out _);
    HttpHelper.RetryReporters.TryRemove(parallelKey, out _);
}

using (var unrelatedActivity = new System.Diagnostics.Activity("UnrelatedInvocation")
    .SetBaggage("finops.turn.id", "other:session").Start())
{
    var failedInner = AIFunctionFactory.Create(async () =>
    {
        await Task.Yield();
        Check(HttpHelper.CurrentTurnUserId() == callbackOwner, "Host binding overrides unrelated inherited baggage");
        return await Task.FromException<string>(new InvalidOperationException("Synthetic tool failure"));
    }, "FailureProbe");
    var observedFailure = false;
    var rebound = SessionBoundTool.Bind([failedInner], callbackOwner, callbackSession);
    try { await ((AIFunction)rebound.Single()).InvokeAsync(new AIFunctionArguments()); }
    catch (InvalidOperationException) { observedFailure = true; }
    Check(observedFailure && ReferenceEquals(System.Diagnostics.Activity.Current, unrelatedActivity)
        && unrelatedActivity.GetBaggageItem("finops.turn.id") == "other:session", "Callback failure restores the caller activity without leaking owner context");

    using var cancelCallback = new CancellationTokenSource();
    var cancelledInner = AIFunctionFactory.Create(async (CancellationToken cancellationToken) =>
    {
        cancelCallback.Cancel();
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return "unreachable";
    }, "CancellationProbe");
    var observedCancellation = false;
    try { await new SessionBoundTool(cancelledInner, callbackOwner, callbackSession).InvokeAsync(new AIFunctionArguments(), cancelCallback.Token); }
    catch (OperationCanceledException) { observedCancellation = true; }
    Check(observedCancellation && ReferenceEquals(System.Diagnostics.Activity.Current, unrelatedActivity),
        "Callback cancellation preserves cancellation semantics and restores activity context");
}

var clientCases = new (string Name, string Url, HttpMethod Method, bool Identified)[]
{
    ("Subscription query", "https://management.azure.com/subscriptions/test/providers/Microsoft.CostManagement/query?api-version=2021-10-01&$top=5000", HttpMethod.Post, true),
    ("Management-group query", "https://management.azure.com/providers/Microsoft.Management/managementGroups/test/providers/Microsoft.CostManagement/query?api-version=2026-06-01", HttpMethod.Post, true),
    ("Forecast", "https://management.azure.com/subscriptions/test/providers/Microsoft.CostManagement/forecast?api-version=2026-06-01", HttpMethod.Post, true),
    ("Case-insensitive path", "https://management.azure.com/subscriptions/test/providers/microsoft.costmanagement/QUERY/", HttpMethod.Post, true),
    ("Metadata GET", "https://management.azure.com/subscriptions/test/providers/Microsoft.Consumption/budgets", HttpMethod.Get, false),
    ("Resource Graph", "https://management.azure.com/providers/Microsoft.ResourceGraph/resources", HttpMethod.Post, false),
    ("Other service", "https://example.invalid/providers/Microsoft.CostManagement/query", HttpMethod.Post, false),
    ("Untrusted ARM-like host", "https://management.azure.com.example.invalid/providers/Microsoft.CostManagement/query", HttpMethod.Post, false),
    ("Insecure URL", "http://management.azure.com/providers/Microsoft.CostManagement/query", HttpMethod.Post, false),
    ("Nonstandard port", "https://management.azure.com:444/providers/Microsoft.CostManagement/query", HttpMethod.Post, false),
    ("Unrelated path", "https://management.azure.com/providers/Microsoft.CostManagement/queryOther", HttpMethod.Post, false),
    ("URL query string only", "https://management.azure.com/test?path=/providers/Microsoft.CostManagement/query", HttpMethod.Post, false),
    ("Unexpected GET", "https://management.azure.com/providers/Microsoft.CostManagement/query", HttpMethod.Get, false)
};
const string clientProbeBody = "{ \"type\":\"ActualCost\", \"dataSet\":{\"granularity\":\"None\"}, \"timeframe\":\"MonthToDate\" }";
var observedClientTypes = new HashSet<string>();
foreach (var test in clientCases)
{
    var expectedToken = Token(Guid.NewGuid(), "client-probe");
    var requests = 0;
    using var clientHttp = new HttpClient(new StubHandler(request =>
    {
        requests++;
        var supplied = request.Headers.TryGetValues("ClientType", out var types) ? types.ToArray() : [];
        Check(test.Identified ? supplied.SequenceEqual(new[] { "AzureFinOpsAgent" }) : supplied.Length == 0,
            $"App-owned ClientType scope: {test.Name}");
        foreach (var value in supplied) observedClientTypes.Add(value);
        Check(request.RequestUri!.AbsoluteUri == new Uri(test.Url).AbsoluteUri && request.Method == test.Method
            && request.Content!.ReadAsStringAsync().GetAwaiter().GetResult() == clientProbeBody
            && request.Headers.Authorization?.Parameter == expectedToken,
            $"Client identification preserves request URI, body, method and caller token: {test.Name}");
        Check(request.Headers.UserAgent.ToString() == "FinOps-Dashboard/1.0"
            && !request.Headers.Contains("Origin") && !request.Headers.Contains("Referer")
            && !request.Headers.Contains("x-ms-command-name"),
            $"Client identification does not impersonate Portal: {test.Name}");
        return JsonResponse(new { properties = new { rows = Array.Empty<object>() } });
    }));
    await HttpHelper.SendCoreAsync(clientHttp, test.Url, expectedToken, null, "test", test.Method, clientProbeBody);
    Check(requests == 1, $"Client identification makes one request: {test.Name}");
}
Check(observedClientTypes.SetEquals(new[] { "AzureFinOpsAgent" }), "ClientType stays constant across users, scopes, API versions and endpoints");
using (var overrideHttp = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("ClientType override reached HTTP"))))
{
    var rejected = await HttpHelper.SendCoreAsync(overrideHttp, clientCases[0].Url,
        Token(Guid.NewGuid(), "client-override"), null, "test", HttpMethod.Post, clientProbeBody,
        extraHeaders: new Dictionary<string, string> { ["cLiEnTtYpE"] = "untrusted-client" });
    Check(rejected.StartsWith("HTTP 400") && !rejected.Contains("untrusted-client"),
        "Host-owned client identity cannot be overridden or echoed");
}

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
coordinator.RecordThrottle(60, "synthetic-caller-one");
Check(coordinator.GetRetryAfterSeconds("synthetic-caller-one") == 60
    && coordinator.GetRetryAfterSeconds("synthetic-caller-two") == 0,
    "B4: Inferred fallback cooldown affects only the rejected caller");
clock.Advance(TimeSpan.FromSeconds(30));
Check(coordinator.GetRetryAfterSeconds("synthetic-caller-one") == 30,
    "B4: Reading a local cooldown does not extend its expiry");
clock.Advance(TimeSpan.FromSeconds(30));
Check(coordinator.GetRetryAfterSeconds("synthetic-caller-one") == 0, "B4: Caller fallback expires");

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
var cacheFaultToken = Token(Guid.NewGuid(), "cache-fault");
using (var faultHttp = new HttpClient(new StubHandler(_ => throw new ArgumentException("Synthetic discovery failure"))))
{
    try { await AzureScopeDiscovery.GetAsync(cacheFaultToken, false, faultHttp); }
    catch (ArgumentException) { }
}
using (var recoveredHttp = new HttpClient(new StubHandler(_ => JsonResponse(new { value = Array.Empty<object>() }))))
    Check((await AzureScopeDiscovery.GetAsync(cacheFaultToken, false, recoveredHttp)).Complete,
        "B8: Faulted discovery task is evicted and the next call can recover");

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
await CostQueryCoordinator.ForToken(cachedToken).Gate.WaitAsync();
try
{
    using var cacheDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
    var fastCached = await HttpHelper.SendCoreAsync(costHttp, costUrl, cachedToken, null, "test", HttpMethod.Post, "{}",
        cancellationToken: cacheDeadline.Token);
    Check(fastCached == original && cachedCalls == 1, "B5: Cache hit bypasses an occupied tenant gate");
}
finally { CostQueryCoordinator.ForToken(cachedToken).Gate.Release(); }
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
Check(tools["QueryAzure"].Description.Contains("api-version=" + AzureApiVersions.CostQuery)
    && tools["QueryAzure"].Description.Contains("alerts=" + AzureApiVersions.CostAlerts)
    && !tools["QueryAzure"].Description.Contains("2026-08-01"), "B14: Tool guidance uses centralized endpoint-specific API versions");
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

using var mgThrottledHttp = new HttpClient(new StubHandler(_ =>
{
    var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"429\"}}") };
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-qpu-consumed", "1");
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-qpu-remaining", "QueriesPerHour:599,QueriesPerMin:59,QueriesPer10Sec:11");
    response.Headers.TryAddWithoutValidation("x-ms-ratelimit-microsoft.costmanagement-entity-retry-after", "31");
    return response;
}));
var mgToken = Token(Guid.NewGuid(), "mg-throttle");
var mgTools = new AzureQueryTools(new UserTokens { AzureToken = mgToken }, mgThrottledHttp).Create().ToDictionary(tool => tool.Name);
var mgThrottled = await Invoke(mgTools["QueryCostsAcrossSubscriptions"], new AIFunctionArguments
{
    ["subscriptionsJson"] = $"[{{\"id\":\"{Guid.NewGuid()}\",\"name\":\"Scope-A\"}}]",
    ["managementGroupId"] = "synthetic-mg",
    ["from"] = "2026-07-08",
    ["to"] = "2026-09-08"
});
using (var mg = JsonDocument.Parse(mgThrottled))
{
    var quota = mg.RootElement.GetProperty("retry").GetProperty("rateLimitHeaders");
    Check(mg.RootElement.GetProperty("status").GetInt32() == 429
        && quota.GetProperty("x-ms-ratelimit-microsoft.costmanagement-entity-retry-after").GetString() == "31"
        && quota.GetProperty("x-ms-ratelimit-microsoft.costmanagement-qpu-remaining").GetString()!.Contains("QueriesPer10Sec:11"),
        "Management-group throttle keeps the quota diagnostics that identify which limit fired");
}
Check(mgTools["QueryCostsAcrossSubscriptions"].Description.Contains("CANNOT answer")
    && mgTools["QueryAzure"].Description.Contains("SPIKE / TREND / REGION")
    && mgTools["QueryAzure"].Description.Contains("ResourceLocation"),
    "Spike, region and trend questions route away from the estate-total tool");
var parseCost = typeof(AzureQueryTools).GetMethod("TryReadCost", BindingFlags.Static | BindingFlags.NonPublic)!;
object?[] parseArguments = ["HTTP 200 OK\n" + JsonSerializer.Serialize(new { properties = new { nextLink = "https://management.azure.com/next" } }), 0d, null, null];
Check(!(bool)parseCost.Invoke(null, parseArguments)!, "Unfinished cost page cannot become a total");
var costColumns = new[] { new { name = "Cost", type = "Number" }, new { name = "Currency", type = "String" } };
var oneScopeArgument = JsonSerializer.Serialize(new[] { new { id = Guid.NewGuid().ToString(), name = "Example" } });
foreach (var status in new[] { 401, 500, 504, 400, 403, 404 })
{
    var subscriptionCalls = 0;
    using var fallbackHttp = new HttpClient(new StubHandler(request =>
    {
        if (!request.RequestUri!.Query.Contains("api-version=" + AzureApiVersions.CostQuery))
            throw new InvalidOperationException("Unexpected cost query API version");
        if (request.RequestUri!.AbsolutePath.Contains("managementGroups"))
        {
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"code\":\"Synthetic\"}}") };
            response.Headers.TryAddWithoutValidation("Retry-After", "120");
            return response;
        }
        subscriptionCalls++;
        return JsonResponse(new { properties = new { columns = costColumns, rows = new[] { new object[] { 0d, "AUD" } } } });
    }));
    var fallbackTool = new AzureQueryTools(new UserTokens { AzureToken = Token(Guid.NewGuid(), "mg-status") }, fallbackHttp)
        .Create().Single(tool => tool.Name == "QueryCostsAcrossSubscriptions");
    using var result = JsonDocument.Parse(await Invoke(fallbackTool, new AIFunctionArguments
    {
        ["subscriptionsJson"] = oneScopeArgument, ["from"] = "2026-01-01", ["to"] = "2026-02-01", ["managementGroupId"] = "example"
    }));
    Check(subscriptionCalls == (status is 400 or 403 or 404 ? 1 : 0), $"B10: HTTP {status} obeys explicit management-group fallback policy");
}
using (var cancellation = new CancellationTokenSource())
{
    var requests = 0;
    using var cancelHttp = new HttpClient(new StubHandler(_ =>
    {
        requests++;
        cancellation.Cancel();
        return JsonResponse(new { properties = new { columns = costColumns, rows = new[] { new object[] { 5d, "AUD" } } } });
    }));
    var cancelTool = new AzureQueryTools(new UserTokens { AzureToken = Token(Guid.NewGuid(), "cancel-cost") }, cancelHttp)
        .Create().Single(tool => tool.Name == "QueryCostsAcrossSubscriptions");
    var cancelled = false;
    try
    {
        await cancelTool.InvokeAsync(new AIFunctionArguments
        {
            ["subscriptionsJson"] = JsonSerializer.Serialize(subscriptionIds.Take(3)), ["from"] = "2026-01-01", ["to"] = "2026-02-01"
        }, cancellation.Token);
    }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && requests == 1, "B6: Cross-subscription cancellation stops later scopes");
}
object?[] emptyArguments = ["HTTP 200 OK\n" + JsonSerializer.Serialize(new { properties = new { columns = costColumns, rows = Array.Empty<object>() } }), 0d, null, null];
Check(!(bool)parseCost.Invoke(null, emptyArguments)! && (string?)emptyArguments[3] == AzureQueryTools.NoCostRows,
    "B1: Empty cost rows are noData, not a measured zero");
object?[] zeroArguments = ["HTTP 200 OK\n" + JsonSerializer.Serialize(new { properties = new { columns = costColumns, rows = new[] { new object[] { 0d, "AUD" } } } }), 0d, null, null];
Check((bool)parseCost.Invoke(null, zeroArguments)! && (double)zeroArguments[1]! == 0 && (string?)zeroArguments[2] == "AUD",
    "B1: An explicit zero row with currency is a measured zero");
var noDataScope = Guid.NewGuid().ToString();
using (var summary = JsonDocument.Parse(AzureQueryTools.BuildCostResponse("test", new[] { (noDataScope, "Example") },
    new Dictionary<string, AzureQueryTools.CostScopeResult>
    { [noDataScope] = new(noDataScope, "Example", 200, null, null, AzureQueryTools.NoCostRows) }, false)))
{
    Check(!summary.RootElement.GetProperty("complete").GetBoolean()
        && summary.RootElement.GetProperty("totalCost").ValueKind == JsonValueKind.Null
        && summary.RootElement.GetProperty("noData").GetInt32() == 1
        && summary.RootElement.GetProperty("succeeded").GetInt32() == 0,
        "B1: Empty estate results never become complete zero spend");
}
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
    if (request.Method == HttpMethod.Get)
    {
        var expected = request.RequestUri!.AbsolutePath.Split('/').Last() switch
        {
            "budgets" => AzureApiVersions.Budgets,
            "exports" => AzureApiVersions.CostExports,
            "alerts" => AzureApiVersions.CostAlerts,
            "scheduledActions" => AzureApiVersions.ScheduledActions,
            _ => throw new InvalidOperationException("Unexpected Crawl endpoint")
        };
        if (!request.RequestUri.Query.Contains("api-version=" + expected)) throw new InvalidOperationException("Crawl API version drift");
    }
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
    (_, _) => throw new InvalidOperationException("Incomplete Crawl must not be persisted"), slowHttp, TimeSpan.FromMilliseconds(100)).Create().Single();
var partialTimer = System.Diagnostics.Stopwatch.StartNew();
var partialCrawl = await Invoke(partialTool, crawlArguments);
using (var result = JsonDocument.Parse(partialCrawl))
{
    Check(!result.RootElement.GetProperty("complete").GetBoolean()
        && result.RootElement.GetProperty("diagnostics").GetProperty("deadlineReached").GetBoolean(),
        "Crawl deadline returns an explicitly incomplete assessment");
    Check(result.RootElement.GetProperty("scores").EnumerateArray().All(score => !score.GetProperty("evidenceComplete").GetBoolean()),
        "Timeouts never appear as verified absence of controls");
    Check(!result.RootElement.GetProperty("historyWriteRequested").GetBoolean(), "B9: Incomplete Crawl does not update score history");
}
Check(partialTimer.Elapsed < TimeSpan.FromSeconds(5) && slowHandler.Requests <= 12 && slowHandler.Active == 0,
    "Crawl cancels queued and in-flight work, preserving the concurrency bound");
Check(!ScoreTools.HasCompleteEvidence("[{\"evidenceComplete\":false}]")
    && ScoreTools.HasCompleteEvidence("[{\"evidenceComplete\":true}]")
    && ScoreTools.HasCompleteEvidence("[{\"score\":2}]"), "B9: History comparisons reject explicit incomplete entries while retaining legacy history");
var stateToken = Token(Guid.NewGuid(), "scope-state");
var states = new[] { "Enabled", "Warned", "PastDue", "Disabled", "Deleted" };
var stateIds = states.Select(_ => Guid.NewGuid().ToString()).ToArray();
using (var stateDiscoveryHttp = new HttpClient(new StubHandler(_ => JsonResponse(new
{
    value = states.Select((state, index) => new { subscriptionId = stateIds[index], displayName = "Example-" + state, state })
}))))
    await AzureScopeDiscovery.GetAsync(stateToken, false, stateDiscoveryHttp);
using (var stateHttp = new HttpClient(new StubHandler(request =>
{
    Check(!stateIds.Skip(3).Any(id => request.RequestUri!.AbsolutePath.Contains(id)), "B7: Automatic Crawl skips disabled/deleted collection reads");
    return request.Method == HttpMethod.Post ? JsonResponse(new { data = Array.Empty<object>() }) : JsonResponse(new { value = Array.Empty<object>() });
})))
{
    var stateTool = new CrawlMaturityTools(new UserTokens { AzureToken = stateToken },
        (_, _) => throw new InvalidOperationException("Excluded estate scopes must not update history"), stateHttp, TimeSpan.FromSeconds(30)).Create().Single();
    using var result = JsonDocument.Parse(await Invoke(stateTool, new AIFunctionArguments { ["subscriptionsJson"] = "all" }));
    Check(result.RootElement.GetProperty("scopeSelection").GetProperty("excluded").GetInt32() == 2
        && result.RootElement.GetProperty("evidence").GetProperty("subscriptionCount").GetInt32() == 3
        && !result.RootElement.GetProperty("complete").GetBoolean(), "B7: Warned and PastDue scopes are retained and exclusions prevent an estate-complete claim");
}
var historicalScopes = new HashSet<string>();
using (var historicalHttp = new HttpClient(new StubHandler(request =>
{
    historicalScopes.Add(request.RequestUri!.Segments[2].Trim('/'));
    return JsonResponse(new { properties = new { columns = new[] { new { name = "Cost" }, new { name = "Currency" } }, rows = new[] { new object[] { 5, "USD" } } } });
})))
{
    var historicalTool = new AzureQueryTools(new UserTokens { AzureToken = stateToken }, historicalHttp).Create()
        .Single(tool => tool.Name == "QueryCostsAcrossSubscriptions");
    using var result = JsonDocument.Parse(await Invoke(historicalTool, new AIFunctionArguments
    {
        ["subscriptionsJson"] = "all", ["from"] = "2026-01-01", ["to"] = "2026-02-01"
    }));
    Check(historicalScopes.SetEquals(stateIds) && result.RootElement.GetProperty("complete").GetBoolean(),
        "B7: Historical costs retain every discovered subscription state");
}
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