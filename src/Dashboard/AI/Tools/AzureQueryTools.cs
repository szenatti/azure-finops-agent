using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Single tool for querying any Azure ARM API using the user's delegated access token.
/// The LLM constructs the URL and optional body; this tool executes the HTTP request.
/// All calls are traced via OpenTelemetry → Application Insights for analysis.
///
/// Security model: read-only. GET is allowed and POST is restricted to known
/// read-only query/report/calculation endpoints. PUT, PATCH, DELETE and mutating
/// action POSTs are blocked at the code level, so the agent cannot create, update or
/// delete Azure resources. The user's Entra RBAC role remains a second boundary —
/// assign Reader / Cost Management Reader.
/// </summary>
public class AzureQueryTools
{
    internal const string NoCostRows = "No matching cost rows were returned; spend is unknown, not zero.";

    // Cached scopes replay without touching the tenant gate, so a wall-clock budget lets a
    // repeated call resume where the previous one stopped instead of re-querying the same head.
    internal static TimeSpan InteractiveCostScopeBudget = TimeSpan.FromSeconds(90);

    // Cost Management dimensions this tool will group by; anything else is rejected before the call.
    internal static readonly string[] CostGroupDimensions =
    {
        "ServiceName", "ResourceGroupName", "MeterCategory", "MeterSubCategory",
        "ResourceLocation", "ResourceType", "ChargeType", "PublisherType", "SubscriptionName"
    };

    private readonly UserTokens _tokens;
    private readonly HttpClient? _http;

    public AzureQueryTools(UserTokens tokens) : this(tokens, null) { }

    internal AzureQueryTools(UserTokens tokens, HttpClient? http)
    {
        _tokens = tokens;
        _http = http;
    }

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(FindSubscriptions, "FindSubscriptions", @"Finds accessible subscriptions by exact name/id, then partial name match, using cached paginated ARM discovery. Use this when a named subscription is not in the connection context. Returns only id, name, state and completeness, at most 50 matches. Duplicate names require clarification. Empty search lists a page; use nextOffset for more. Never use shell tools or GET /subscriptions to resolve names.");
        yield return AIFunctionFactory.Create(QueryAzure, "QueryAzure", @"Queries Azure ARM REST APIs (https://management.azure.com) using the signed-in user's delegated token. Returns raw JSON.
Methods: GET, plus allowlisted read-only POST query endpoints. This agent is READ-ONLY: PUT, PATCH, DELETE and mutating action POSTs are blocked at the code level and return HTTP 403, so never attempt a write — deliver it via GenerateScript instead. The user's Entra RBAC is a second boundary.

Use standard ARM URL conventions; you know the resource providers and current api-versions. Common surfaces: Microsoft.CostManagement (query/forecast/exports), Microsoft.Consumption (budgets/pricesheets/reservation*), Microsoft.Capacity (reservations), Microsoft.BillingBenefits (savingsPlans), Microsoft.Advisor (recommendations), Microsoft.ResourceGraph (KQL across subs), Microsoft.Insights (metrics/diagnostics/autoscale), Microsoft.Compute, Microsoft.ContainerService, Microsoft.Network, Microsoft.Storage, Microsoft.Sql, Microsoft.Web, Microsoft.OperationalInsights, Microsoft.MachineLearningServices, Microsoft.CognitiveServices, Microsoft.App, Microsoft.Authorization (RBAC/Policy/Locks), Microsoft.Management, Microsoft.Quota, Microsoft.Carbon, Microsoft.Migrate, Microsoft.Support, Microsoft.ResourceHealth, Microsoft.Security.

=== NON-OBVIOUS RULES (read carefully) ===
{scope} GRAMMAR (REQUIRED for ALL Microsoft.CostManagement, Microsoft.Consumption, and Microsoft.CostManagement/budgets paths) — must be ONE of:
  /subscriptions/{subId}
  /subscriptions/{subId}/resourceGroups/{rgName}
  /providers/Microsoft.Management/managementGroups/{mgId}
  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}[/billingProfiles/{id}|/invoiceSections/{id}]
Never bare /providers/Microsoft.CostManagement/... — that returns 400.

COST MANAGEMENT QUERY: use api-version=" + AzureApiVersions.CostQuery + @". ALWAYS group by a real dimension (ServiceName, ResourceGroupName, MeterCategory). Do NOT add 'UsageDate' to the grouping array — it's a response column, not a dimension; use granularity=""Daily"" for per-day. Never request raw ungrouped cost data. For a plain total across all subscriptions, use QueryCostsAcrossSubscriptions with subscriptionsJson='all'; never fan out one query per subscription. Budget currentSpend is last evaluated spend, NOT live cost and NOT a service breakdown. GET /subscriptions returns a compact page; use FindSubscriptions for name resolution.
SPIKE / TREND QUESTIONS: do NOT use QueryCostsAcrossSubscriptions for a per-day series — it has no daily granularity. Issue ONE query here at the narrowest scope that covers the question, with granularity=""Daily"" for a per-day series and dataset.filter for dimensions. Region example: {""dimensions"":{""name"":""ResourceLocation"",""operator"":""In"",""values"":[""East US"",""West US 2""]}}. Grouping accepts at most 2 dimensions. ResourceLocation values are Cost Management's own labels; if a region filter returns no rows, re-run grouped by ResourceLocation to read the actual values instead of guessing, and report zero rows as unknown, never as zero spend. For a SERVICE / RESOURCE-GROUP / REGION BREAKDOWN ACROSS MANY SUBSCRIPTIONS, use QueryCostsAcrossSubscriptions with its groupBy parameter instead of querying one scope here.
MANAGEMENT-GROUP SCOPE: NEVER use a management group — including the tenant root group — as a stand-in for 'all subscriptions'. A rollup silently OMITS every subscription Cost Management cannot aggregate for this caller (documented behaviour for CSP subscriptions) and returns HTTP 200 for the remainder, so a small total is indistinguishable from a complete one. Answer estate-wide questions with QueryCostsAcrossSubscriptions using subscriptionsJson='all' — that tool needs no billing-account access and is ALWAYS available, so never refuse an estate-wide question because no billing account is accessible. Management-group scope is unsupported for Microsoft Customer Agreement and CSP accounts, and even on an Enterprise Agreement it can return 'Management group ... does not have any valid subscriptions' when the group holds no subscriptions Cost Management can aggregate for this caller. That is a deterministic HTTP 400, never a throttle: do not retry it, fall back to subscription scope and say the aggregate was unavailable. Management-group totals cover usage charges only and EXCLUDE reservations, savings plans and Marketplace purchases, so they are not comparable with billing-account totals; state the scope and both exclusions whenever you report one.
OTHER COST API VERSIONS: forecast=" + AzureApiVersions.CostForecast + "; exports=" + AzureApiVersions.CostExports + "; alerts=" + AzureApiVersions.CostAlerts + "; scheduledActions=" + AzureApiVersions.ScheduledActions + "; Consumption budgets=" + AzureApiVersions.Budgets + @". Each endpoint has its own supported version; do not invent a newer one. Preserve versions explicitly requested for controlled comparisons.

THROTTLING: Cost Management /query and /forecast are aggressively throttled per-tenant. Interactive queries make at most one short retry; other transient calls retain the standard retry policy. Do NOT call multiple CostManagement endpoints in parallel from the same turn — Resource Graph and Advisor parallelize fine. If a call still returns HTTP 429, do not make another Cost Management call in the same turn; report the throttle and offer to retry later.

RESOURCE GRAPH (POST /providers/Microsoft.ResourceGraph/resources): always use 'project' to limit columns and 'take N' to limit rows — bare 'top N' is a ParserFailure, because Kusto requires 'top N by <column>'. Use one pipeline; Azure Resource Graph does not accept multi-statement `let ...; let ...;` queries.

SPOT QUOTA: Spot/low-priority VM quota is a SINGLE regional bucket called 'lowPriorityCores' (NOT per VM family) — covers ALL spot VMs including H100/A100. Standard quotas are per-family ('standardNDSH100v5Family', 'StandardNCadsH100v5Family'). Microsoft.Quota requires RP registration (PUT /subscriptions/{subId}/providers/Microsoft.Quota/register) — fall back to GET .../Microsoft.Compute/locations/{region}/usages if not registered.

FOUNDRY / AZURE OPENAI QUOTA: Lives under Microsoft.CognitiveServices, NOT prices.azure.com. Per-region quota: GET /subscriptions/{id}/providers/Microsoft.CognitiveServices/locations/{region}/usages — returns name.value entries like 'OpenAI.GlobalStandard.gpt-5.6-sol'. For deployments: GET .../accounts/{name}/deployments returns properties.model.name + sku.capacity (TPM in thousands).

MIGRATE: Use resource type 'assessmentProjects' (NOT 'migrateProjects' — returns 404).

CONSUMPTION DEPRECATIONS: usageDetails → use Microsoft.CostManagement/generateCostDetailsReport. reservationDetails → use Microsoft.CostManagement/generateReservationDetailsReport.

For public retail pricing use https://prices.azure.com (no auth) with ?$filter=armRegionName eq '...' and serviceName eq '...' and armSkuName eq '...'&$top=20.");

        yield return AIFunctionFactory.Create(QueryCostsAcrossSubscriptions, "QueryCostsAcrossSubscriptions", @"Gets COST TOTALS across many subscriptions in ONE agent tool call, optionally broken down by a dimension, plus coverage counts and up to 50 subscription details. Use this whenever the question is 'how much did we spend' across more than one subscription; never loop QueryAzure yourself. It needs no billing-account access, so never refuse an estate-wide question because no billing account is available. Cached results may be up to five minutes old and Azure cost ingestion may lag usage.
GROUPING — set groupBy to break the estate total down: ServiceName, ResourceGroupName, MeterCategory, MeterSubCategory, ResourceLocation, ResourceType, ChargeType, PublisherType or SubscriptionName. The `byGroup` array then holds the cross-subscription totals per group and currency, cheapest field to chart. Omit groupBy for a plain total per subscription. groupBy has no daily granularity; route per-day series to a single scoped QueryAzure query instead. Supplying groupBy disables the management-group aggregate shortcut.
Input subscriptionsJson: 'all' for all accessible subscriptions (discovered by the host, never copy a truncated context list), or an explicit JSON array of selected {id,name} scopes. Input managementGroupId: an optional verified containing management group. Dates are yyyy-MM-dd; `to` is the exclusive end date.
    Uses Cost Management only, never budget evaluations as a live-cost substitute. It tries one supplied management-group aggregate, then queries subscriptions sequentially until a wall-clock budget is reached. Already-queried subscriptions are cached for five minutes and replay instantly, so on a large estate calling this tool again within that window RESUMES from where it stopped — repeat until `unattempted` reaches 0, reporting coverage each time. Stops immediately on throttling; reports partial coverage and unattempted scopes, never a complete total for partial data. On HTTP 429 the `retry` field names the Azure quota that fired — report it verbatim. Never call this tool twice in one turn after a 429.");


        yield return AIFunctionFactory.Create(BulkAzureRequest, "BulkAzureRequest", @"Executes MANY Azure ARM READ requests in ONE tool call, in parallel, server-side. Use this whenever you would otherwise loop QueryAzure for the same kind of read across multiple resources (per-resource configuration, properties, inventory detail, quota/usage fan-out).
Input: requestsJson = JSON array of {""method"":""GET"",""path"":""/...?api-version=...""}. POST is accepted only for the same read-only allowlist as QueryAzure.
Optional: parallelism (default 20, max 50), stopOnFirstError (default false).
Returns ONE compact JSON summary: {""total"":N,""succeeded"":X,""failed"":Y,""durationMs"":Z,""failures"":[{""index"":i,""status"":code,""path"":""..."",""error"":""...""}],""successSamples"":[{""path"":""..."",""name"":""...""}]}.
READ-ONLY: PUT, PATCH, DELETE and mutating action POSTs are blocked at the code level and fail per request with HTTP 403. Throttling-aware: 429 retries are handled per request.
Use this INSTEAD of looping QueryAzure when you have ≥5 similar reads. Build the request list from your prior Resource Graph discovery query in the same turn.");
    }

    private async Task<string> FindSubscriptions(
        [Description("Subscription name or id; exact matches are preferred, otherwise partial name matches. Empty lists subscriptions.")] string search = "",
        [Description("Offset from nextOffset in a previous result, default 0")] string offset = "0")
    {
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token)) return HttpHelper.TokenMissing("AzureToken", null, "azure.subscriptions");
        if (!int.TryParse(offset, out var pageOffset) || pageOffset < 0 || pageOffset > 10000)
            return "HTTP 400 BadRequest\nOffset must be between 0 and 10000.";
        var discovery = await AzureScopeDiscovery.SubscriptionsAsync(token);
        return AzureScopeDiscovery.Find(discovery, search, pageOffset);
    }

    private async Task<string> QueryAzure(
        [Description("HTTP method: GET, or POST for allowlisted read-only queries (PUT, PATCH and DELETE are blocked)")] string method,
        [Description("API path starting with /, e.g. /subscriptions?api-version=2022-12-01")] string path,
        [Description("Optional JSON request body for read-only POST queries. Omit or leave empty for GET.")] string? body = null)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("QueryAzure");
        activity?.SetTag("azure.method", method);
        activity?.SetTag("azure.path", path);
        activity?.SetTag("azure.has_body", !string.IsNullOrWhiteSpace(body));
        if (!string.IsNullOrWhiteSpace(body))
            activity?.SetTag("azure.body", body.Length > 2000 ? body[..2000] + "..." : body);

        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "azure");

        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            activity?.SetTag("azure.result", "invalid_path");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid path");
            return $"HTTP 400 BadRequest\nInvalid path: '{path}'. Path must start with /.";
        }

        // Scope-prefix preflight: catches the #1 production failure pattern observed in App Insights —
        // the LLM emitting bare /providers/Microsoft.CostManagement|Consumption|... paths without the
        // required {scope} prefix (subscriptions / resourceGroups / managementGroups / billingAccounts).
        // ARM responds 404 InvalidResourceType in that case; we return a precise 400 with the grammar
        // so the LLM corrects on the next turn instead of burning a round-trip.
        var scopeError = ValidateScopePrefix(path);
        if (scopeError is not null)
        {
            activity?.SetTag("azure.result", "missing_scope");
            activity?.SetStatus(ActivityStatusCode.Error, "Missing scope prefix");
            return scopeError;
        }

        var (httpMethod, methodError) = HttpHelper.ResolveMethod(method, activity, "azure", allowReadOnlyPost: true);
        if (methodError is not null) return methodError;
        if (httpMethod == HttpMethod.Get && path.Split('?')[0].TrimEnd('/').Equals("/subscriptions", StringComparison.OrdinalIgnoreCase))
            return await FindSubscriptions();
        if (httpMethod == HttpMethod.Post)
        {
            var postError = ValidateReadOnlyPostPath(path, activity);
            if (postError is not null) return postError;
        }

        var hasBody = !string.IsNullOrWhiteSpace(body);
        var response = await HttpHelper.SendWithRetryAsync(
            $"https://management.azure.com{path}",
            token, activity, "azure",
            method: httpMethod,
            jsonBody: hasBody && httpMethod != HttpMethod.Get ? body : null,
            includeTimestamp: true);
        return AppendManagementGroupCoverageCaveat(path, response);
    }

    /// <summary>
    /// A successful management-group rollup is not evidence of estate-wide coverage: Cost Management
    /// drops subscriptions it cannot aggregate for the caller instead of failing, so the model needs
    /// the caveat attached to the data rather than inferred from the scope.
    /// </summary>
    internal static string AppendManagementGroupCoverageCaveat(string path, string response)
    {
        if (!response.StartsWith("HTTP 200", StringComparison.Ordinal)) return response;
        var pathOnly = path.Split('?')[0];
        if (!pathOnly.Contains("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase)
            || !pathOnly.Contains("/providers/Microsoft.CostManagement/", StringComparison.OrdinalIgnoreCase))
            return response;
        return response + "\n\nCOVERAGE WARNING: management-group scope. This rollup silently excludes every "
            + "subscription Cost Management cannot aggregate for this caller, and always excludes reservations, "
            + "savings plans and Marketplace purchases. It is NOT an all-subscription total and must never be "
            + "labelled one. For estate-wide spend use QueryCostsAcrossSubscriptions with subscriptionsJson='all', "
            + "or a billing-account scope. If you report this number, state the scope and both exclusions.";
    }

    private async Task<string> QueryCostsAcrossSubscriptions(
        [Description("Use 'all' for all accessible subscriptions, or a JSON array of explicitly selected objects with id and name fields")] string subscriptionsJson,
        [Description("Inclusive start date in yyyy-MM-dd format")] string from,
        [Description("Exclusive end date in yyyy-MM-dd format")] string to,
        [Description("Optional management-group id or full ARM path from the connection context")] string? managementGroupId = null,
        [Description("Optional Cost Management dimension to break the total down by, e.g. ServiceName or ResourceGroupName. Omit for a plain total per subscription.")] string? groupBy = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = HttpHelper.Telemetry.StartActivity("QueryCostsAcrossSubscriptions");
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "cost.cross_subscription");

        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var fromDate)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var toDate)
            || fromDate >= toDate
            || toDate.DayNumber - fromDate.DayNumber > 366)
        {
            return "HTTP 400 BadRequest\nfrom/to must be valid yyyy-MM-dd dates, from must precede to, and the range must not exceed 366 days.";
        }

        var scopes = new List<(string Id, string Name)>();
        if (subscriptionsJson.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var discovery = await AzureScopeDiscovery.SubscriptionsAsync(token, cancellationToken);
            if (!discovery.Complete)
                return JsonSerializer.Serialize(new { complete = false, source = "scopeDiscovery", detail = discovery.Error });
            subscriptionsJson = JsonSerializer.Serialize(discovery.Scopes
                .OrderBy(scope => string.Equals(scope.State, "Enabled", StringComparison.OrdinalIgnoreCase) ? 0
                    : string.Equals(scope.State, "Warned", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                .Select(scope => new { id = scope.Id, name = scope.Name }));
        }
        try
        {
            using var doc = JsonDocument.Parse(subscriptionsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "HTTP 400 BadRequest\nsubscriptionsJson must be a JSON array.";
            if (doc.RootElement.GetArrayLength() > 10000)
                return "HTTP 400 BadRequest\nsubscriptionsJson supports at most 10000 entries; use a narrower scope.";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rawId = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                rawId = rawId?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var parsedId)) continue;

                var name = item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString()
                    : null;
                var canonicalId = parsedId.ToString();
                if (!seen.Add(canonicalId)) continue;
                scopes.Add((canonicalId, string.IsNullOrWhiteSpace(name) ? canonicalId : name!));
            }
        }
        catch (JsonException ex)
        {
            return $"HTTP 400 BadRequest\nInvalid subscriptionsJson: {ex.Message}";
        }

        if (scopes.Count == 0)
            return "HTTP 400 BadRequest\nsubscriptionsJson contained no valid subscription IDs.";

        string? groupDimension = null;
        if (!string.IsNullOrWhiteSpace(groupBy))
        {
            groupDimension = CostGroupDimensions.FirstOrDefault(
                dimension => dimension.Equals(groupBy.Trim(), StringComparison.OrdinalIgnoreCase));
            if (groupDimension is null)
                return $"HTTP 400 BadRequest\ngroupBy must be one of: {string.Join(", ", CostGroupDimensions)}.";
        }

        var body = JsonSerializer.Serialize(new
        {
            type = "ActualCost",
            timeframe = "Custom",
            timePeriod = new
            {
                from = fromDate.ToString("yyyy-MM-dd"),
                to = toDate.ToString("yyyy-MM-dd")
            },
            dataset = groupDimension is null
                ? new
                {
                    granularity = "None",
                    aggregation = new { totalCost = new { name = "Cost", function = "Sum" } }
                }
                : (object)new
                {
                    granularity = "None",
                    aggregation = new { totalCost = new { name = "Cost", function = "Sum" } },
                    grouping = new[] { new { type = "Dimension", name = groupDimension } }
                }
        });

        Dictionary<string, CostScopeResult>? aggregateResults = null;
        string? managementGroupError = null;

        // Prefer one aggregate call. An accessible management group is not
        // guaranteed to contain the delegated subscriptions, so only 400/403/404
        // fall back; a 429 must stop immediately to protect the tenant quota.
        // The aggregate groups by subscription, so it cannot serve a dimension breakdown.
        if (!string.IsNullOrWhiteSpace(managementGroupId) && groupDimension is null)
        {
            var mgName = managementGroupId.Trim().TrimEnd('/').Split('/').Last();
            if (mgName.Length > 0)
            {
                var mgBody = JsonSerializer.Serialize(new
                {
                    type = "ActualCost",
                    timeframe = "Custom",
                    timePeriod = new
                    {
                        from = fromDate.ToString("yyyy-MM-dd"),
                        to = toDate.ToString("yyyy-MM-dd")
                    },
                    dataset = new
                    {
                        granularity = "None",
                        aggregation = new { totalCost = new { name = "Cost", function = "Sum" } },
                        grouping = new[]
                        {
                            new { type = "Dimension", name = "SubscriptionId" },
                            new { type = "Dimension", name = "SubscriptionName" }
                        }
                    }
                });
                var mgUrl = $"https://management.azure.com/providers/Microsoft.Management/managementGroups/{Uri.EscapeDataString(mgName)}/providers/Microsoft.CostManagement/query?api-version={AzureApiVersions.CostQuery}";
                var mgResponse = await HttpHelper.SendCoreAsync(
                    _http, mgUrl, token, activity, "cost.cross_subscription.mg",
                    method: HttpMethod.Post, jsonBody: mgBody, cancellationToken: cancellationToken);
                if (mgResponse.StartsWith("HTTP 200", StringComparison.Ordinal))
                {
                    var aggregate = ParseAggregateCostResponse(mgResponse, scopes);
                    if (aggregate.Error is null && aggregate.Results.Count == scopes.Count)
                        return BuildCostResponse("managementGroup", scopes, aggregate.Results, false);

                    // Keep any requested subscriptions returned by the aggregate
                    // and query only the missing scopes below. Extra management-
                    // group subscriptions are ignored by ParseAggregateCostResponse.
                    aggregateResults = aggregate.Error is null
                        ? aggregate.Results
                        : new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase);
                }
                var mgStatus = ParseStatusCode(mgResponse);
                if (mgStatus != 200 && mgStatus is not (400 or 403 or 404))
                    return JsonSerializer.Serialize(new
                    {
                        complete = false,
                        source = "managementGroup",
                        status = mgStatus,
                        throttled = mgStatus == 429,
                        attempted = 1,
                        unattempted = scopes.Count,
                        subscriptionCount = scopes.Count,
                        detail = FirstLineAndBody(mgResponse, 500),
                        retry = ThrottleDiagnostics(mgResponse)
                    });
                // A silent fallback hides deterministic scope errors such as
                // "does not have any valid subscriptions", which never recover on retry.
                if (mgStatus is 400 or 403 or 404)
                    managementGroupError = FirstLineAndBody(mgResponse, 300);
            }
        }

        aggregateResults ??= new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase);
        var reusedAggregateResults = aggregateResults.Count > 0;
        var resultsById = aggregateResults;
        var remainingScopes = scopes.Where(s => !resultsById.ContainsKey(s.Id)).ToList();
        var throttled = false;
        JsonNode? throttleDiagnostics = null;
        var budget = Stopwatch.StartNew();
        var groupTotals = groupDimension is null ? null : new Dictionary<(string Group, string Currency), double>();

        for (var i = 0; i < remainingScopes.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = remainingScopes[i];
            if (budget.Elapsed >= InteractiveCostScopeBudget)
            {
                resultsById[scope.Id] = new(scope.Id, scope.Name, 0, null, null,
                    "not attempted: interactive time budget reached; ask again within five minutes to resume from cache, or use a billing-account scope or cost export");
                continue;
            }
            var url = $"https://management.azure.com/subscriptions/{scope.Id}/providers/Microsoft.CostManagement/query?api-version={AzureApiVersions.CostQuery}";
            var response = await HttpHelper.SendCoreAsync(
                _http, url, token, activity, "cost.cross_subscription.subscription",
                method: HttpMethod.Post, jsonBody: body, cancellationToken: cancellationToken);

            string? parseError = null;
            if (response.StartsWith("HTTP 200", StringComparison.Ordinal))
            {
                if (groupDimension is null)
                {
                    if (TryReadCost(response, out var cost, out var currency, out parseError))
                    {
                        resultsById[scope.Id] = new(scope.Id, scope.Name, 200, cost, currency, null);
                        continue;
                    }
                }
                else if (TryReadCostRows(response, groupDimension, out var costRows, out parseError))
                {
                    foreach (var costRow in costRows)
                    {
                        var key = (costRow.Group ?? "(unattributed)", costRow.Currency);
                        groupTotals![key] = groupTotals.TryGetValue(key, out var running)
                            ? running + costRow.Cost : costRow.Cost;
                    }
                    var scopeCurrencies = costRows
                        .Select(costRow => costRow.Currency)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    resultsById[scope.Id] = new(scope.Id, scope.Name, 200,
                        costRows.Sum(costRow => costRow.Cost),
                        scopeCurrencies.Count == 1 ? scopeCurrencies[0] : null,
                        null);
                    continue;
                }
            }

            var status = ParseStatusCode(response);
            resultsById[scope.Id] = new(
                scope.Id,
                scope.Name,
                status,
                null,
                null,
                status == 200 ? parseError : FirstLineAndBody(response, 400));
            if (status == 429)
            {
                throttled = true;
                throttleDiagnostics = ThrottleDiagnostics(response);
                for (var j = i + 1; j < remainingScopes.Count; j++)
                {
                    var unattempted = remainingScopes[j];
                    resultsById[unattempted.Id] = new(
                        unattempted.Id,
                        unattempted.Name,
                        0,
                        null,
                        null,
                        "not attempted after tenant throttle");
                }
                break;
            }
        }

        var source = reusedAggregateResults ? "managementGroup+subscriptions" : "subscriptions";
        return BuildCostResponse(source, scopes, resultsById, throttled, throttleDiagnostics, managementGroupError,
            groupTotals, groupDimension);
    }

    internal static BudgetSpend ReadUnfilteredBudgetSpend(
        (string Id, string Name) scope,
        string response,
        DateOnly periodStart,
        DateOnly periodEndInclusive)
    {
        if (ParseStatusCode(response) != 200)
            return new(scope.Id, scope.Name, false, 0, null, null);

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            if (!doc.RootElement.TryGetProperty("value", out var values)
                || values.ValueKind != JsonValueKind.Array)
                return new(scope.Id, scope.Name, false, 0, null, null);

            var candidates = new List<(string? Name, double Cost, string? Currency)>();
            foreach (var budget in values.EnumerateArray())
            {
                if (!budget.TryGetProperty("properties", out var props)) continue;
                if (!props.TryGetProperty("category", out var category)
                    || !string.Equals(category.GetString(), "Cost", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!props.TryGetProperty("timeGrain", out var grain)
                    || !string.Equals(grain.GetString(), "Monthly", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (props.TryGetProperty("filter", out var filter) && HasEffectiveBudgetFilter(filter))
                    continue;
                if (!props.TryGetProperty("timePeriod", out var timePeriod)
                    || !timePeriod.TryGetProperty("startDate", out var startDateElement)
                    || !timePeriod.TryGetProperty("endDate", out var endDateElement)
                    || !TryReadDate(startDateElement, out var budgetStart)
                    || !TryReadDate(endDateElement, out var budgetEnd)
                    || budgetStart > periodStart
                    || budgetEnd < periodEndInclusive)
                    continue;
                if (!props.TryGetProperty("currentSpend", out var current)
                    || !current.TryGetProperty("amount", out var amount)
                    || !amount.TryGetDouble(out var cost)
                    || !double.IsFinite(cost)
                    || cost < 0)
                    continue;
                var currency = current.TryGetProperty("unit", out var unit) ? unit.GetString() : null;
                if (string.IsNullOrWhiteSpace(currency)) continue;
                var name = budget.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                candidates.Add((name, cost, currency));
            }

            if (candidates.Count == 0)
                return new(scope.Id, scope.Name, false, 0, null, null);

            // Multiple unfiltered subscription budgets should expose the same
            // subscription currentSpend. If they disagree, do not guess—fall
            // through to the authoritative Cost Management query path.
            var distinctCosts = candidates.Select(c => Math.Round(c.Cost, 6)).Distinct().ToArray();
            var distinctCurrencies = candidates
                .Select(c => c.Currency)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinctCosts.Length != 1 || distinctCurrencies.Length != 1)
                return new(scope.Id, scope.Name, false, 0, null, null);

            return new(scope.Id, scope.Name, true, candidates[0].Cost, distinctCurrencies[0], candidates[0].Name);
        }
        catch (JsonException)
        {
            return new(scope.Id, scope.Name, false, 0, null, null);
        }
    }

    private static bool HasEffectiveBudgetFilter(JsonElement filter)
    {
        if (filter.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        if (filter.ValueKind != JsonValueKind.Object) return true;

        foreach (var property in filter.EnumerateObject())
        {
            var value = property.Value;
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()) continue;
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) continue;
            return true;
        }
        return false;
    }

    private static bool TryReadDate(JsonElement element, out DateOnly date)
    {
        date = default;
        var raw = element.GetString();
        return DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var timestamp)
            && (date = DateOnly.FromDateTime(timestamp.UtcDateTime)) != default;
    }

    private static AggregateCostResult ParseAggregateCostResponse(
        string response,
        IReadOnlyList<(string Id, string Name)> expectedScopes)
    {
        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var props = doc.RootElement.GetProperty("properties");
            if (props.TryGetProperty("nextLink", out var continuation) && !string.IsNullOrWhiteSpace(continuation.GetString()))
                return new(new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase),
                    "Aggregate cost response is paginated; this page alone is not a complete total.");
            var columns = props.GetProperty("columns").EnumerateArray()
                .Select((c, i) => (Name: c.GetProperty("name").GetString() ?? "", Index: i))
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
            var costIndex = columns.TryGetValue("Cost", out var ci) ? ci : columns["PreTaxCost"];
            var idIndex = columns["SubscriptionId"];
            var currencyIndex = columns.TryGetValue("Currency", out var cui) ? cui : -1;
            var expectedById = expectedScopes.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            var accumulators = new Dictionary<string, (double Cost, HashSet<string> Currencies)>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in props.GetProperty("rows").EnumerateArray())
            {
                var rawId = row[idIndex].GetString()?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var parsedId)) continue;
                var id = parsedId.ToString();
                if (!expectedById.ContainsKey(id)) continue;

                var cost = row[costIndex].GetDouble();
                var currency = currencyIndex >= 0 ? row[currencyIndex].GetString() : null;
                if (!double.IsFinite(cost)) continue;

                if (!accumulators.TryGetValue(id, out var current))
                    current = (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                current.Cost += cost;
                if (!string.IsNullOrWhiteSpace(currency)) current.Currencies.Add(currency);
                accumulators[id] = current;
            }

            var results = new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, value) in accumulators)
            {
                // A subscription should have one billing currency. If the
                // aggregate says otherwise (or omits currency for non-zero
                // cost), leave it missing so the per-subscription query path
                // can validate it independently.
                if (value.Currencies.Count != 1)
                    continue;
                var scope = expectedById[id];
                results[id] = new(
                    id,
                    scope.Name,
                    200,
                    value.Cost,
                    value.Currencies.Count == 1 ? value.Currencies.Single() : null,
                    null);
            }

            return new(results, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(
                new Dictionary<string, CostScopeResult>(StringComparer.OrdinalIgnoreCase),
                ex.Message);
        }
    }

    internal static string BuildCostResponse(
        string source,
        IReadOnlyList<(string Id, string Name)> scopes,
        IReadOnlyDictionary<string, CostScopeResult> resultsById,
        bool throttled,
        JsonNode? retry = null,
        string? managementGroupError = null,
        IReadOnlyDictionary<(string Group, string Currency), double>? groupTotals = null,
        string? groupedBy = null)
    {
        var orderedResults = scopes.Select(scope =>
            resultsById.TryGetValue(scope.Id, out var result)
                ? result
                : new CostScopeResult(scope.Id, scope.Name, 0, null, null, "not returned")).ToList();
        var succeeded = orderedResults.Count(r => r.Status == 200 && r.Cost is not null);
        var unknownCurrencyCost = orderedResults.Any(r =>
            r.Status == 200 && r.Cost is not null && string.IsNullOrWhiteSpace(r.Currency));
        var totalsByCurrency = orderedResults
            .Where(r => r.Status == 200 && r.Cost is not null && !string.IsNullOrWhiteSpace(r.Currency))
            .GroupBy(r => r.Currency!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(r => r.Cost!.Value), 6), StringComparer.OrdinalIgnoreCase);
        var complete = scopes.Count > 0 && succeeded == scopes.Count && !unknownCurrencyCost;
        var singleCurrency = totalsByCurrency.Count == 1 ? totalsByCurrency.Keys.Single() : null;
        var safeAggregate = totalsByCurrency.Count <= 1 && !unknownCurrencyCost;
        var summedCost = safeAggregate
            ? orderedResults.Where(r => r.Status == 200 && r.Cost is not null).Sum(r => r.Cost!.Value)
            : (double?)null;

        return JsonSerializer.Serialize(new
        {
            complete,
            source,
            throttled,
            retry,
            managementGroupError,
            subscriptionCount = scopes.Count,
            succeeded,
            noData = orderedResults.Count(result => result.Status == 200 && result.Cost is null && result.Error == NoCostRows),
            failed = orderedResults.Count(result => result.Status != 0 && (result.Status != 200 || result.Cost is null)
                && !(result.Status == 200 && result.Error == NoCostRows)),
            unattempted = orderedResults.Count(result => result.Status == 0),
            resultsTruncated = orderedResults.Count > 50,
            freshness = "Cost Management queries may be cached for five minutes; upstream cost ingestion may lag usage.",
            mixedCurrencies = totalsByCurrency.Count > 1,
            totalCost = complete && safeAggregate ? Math.Round(summedCost!.Value, 6) : (double?)null,
            partialCost = !complete && safeAggregate && succeeded > 0 ? Math.Round(summedCost!.Value, 6) : (double?)null,
            currency = singleCurrency,
            totalsByCurrency,
            groupedBy,
            byGroupTruncated = groupTotals is not null && groupTotals.Count > 100,
            byGroup = groupTotals?
                .OrderByDescending(entry => entry.Value)
                .Take(100)
                .Select(entry => new
                {
                    group = entry.Key.Group,
                    currency = entry.Key.Currency,
                    cost = Math.Round(entry.Value, 6)
                }),
            results = orderedResults.OrderBy(result => result.Status == 200 && result.Cost is not null ? 0 : result.Status != 0 ? 1 : 2)
                .ThenByDescending(result => result.Cost ?? 0).Take(50).Select(r => new
            {
                subscriptionId = r.SubscriptionId,
                subscriptionName = r.SubscriptionName,
                status = r.Status,
                cost = r.Cost,
                currency = r.Currency,
                outcome = r.Status == 200 && r.Error == NoCostRows ? "noData"
                    : r.Status == 200 && r.Cost is not null ? "measured" : r.Status == 0 ? "notAttempted" : "failed",
                error = r.Error
            })
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryReadCost(string response, out double cost, out string? currency, out string? error)
    {
        cost = 0;
        currency = null;
        if (!TryReadCostRows(response, null, out var rows, out error)) return false;
        var currencies = rows.Select(row => row.Currency).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (currencies.Count > 1)
        {
            error = "Cost response contained more than one currency.";
            return false;
        }
        cost = rows.Sum(row => row.Cost);
        currency = currencies.Single();
        return true;
    }

    private static bool TryReadCostRows(string response, string? groupBy, out List<CostRow> rows, out string? error)
    {
        rows = new List<CostRow>();
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var props = doc.RootElement.GetProperty("properties");
            if (props.TryGetProperty("nextLink", out var continuation) && !string.IsNullOrWhiteSpace(continuation.GetString()))
            {
                error = "Cost response is paginated; this page alone is not a complete total.";
                return false;
            }
            var columns = props.GetProperty("columns").EnumerateArray()
                .Select((c, i) => (Name: c.GetProperty("name").GetString() ?? "", Index: i))
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
            var costIndex = columns.TryGetValue("Cost", out var ci) ? ci : columns["PreTaxCost"];
            var currencyIndex = columns.TryGetValue("Currency", out var cui) ? cui : -1;
            var groupIndex = groupBy is not null && columns.TryGetValue(groupBy, out var gi) ? gi : -1;
            var rowsElement = props.GetProperty("rows");
            if (rowsElement.GetArrayLength() == 0)
            {
                error = NoCostRows;
                return false;
            }
            foreach (var row in rowsElement.EnumerateArray())
            {
                var rowCost = row[costIndex].GetDouble();
                if (!double.IsFinite(rowCost))
                {
                    error = "Cost response contained a non-finite value.";
                    return false;
                }
                var rowCurrency = currencyIndex >= 0 ? row[currencyIndex].GetString() : null;
                if (string.IsNullOrWhiteSpace(rowCurrency))
                {
                    error = "Cost response omitted currency.";
                    return false;
                }
                var group = groupIndex >= 0 ? row[groupIndex].GetString() : null;
                rows.Add(new CostRow(string.IsNullOrWhiteSpace(group) ? null : group, rowCost, rowCurrency));
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            rows.Clear();
            error = ex.Message;
            return false;
        }
    }

    private static string ResponseBody(string response)
    {
        var firstNewline = response.IndexOf('\n');
        return firstNewline >= 0 ? response[(firstNewline + 1)..] : "";
    }

    private static int ParseStatusCode(string response)
    {
        var firstLine = response.Split('\n', 2)[0];
        var parts = firstLine.Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var status) ? status : 0;
    }

    private static string FirstLineAndBody(string response, int maxChars) =>
        response.Length <= maxChars ? response : response[..maxChars];

    // Returned whole, never truncated: this block names the Azure quota that fired.
    private static JsonNode? ThrottleDiagnostics(string response)
    {
        try
        {
            return JsonNode.Parse(ResponseBody(response)) is JsonObject root
                ? root["finopsRetry"]?.DeepClone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed record BudgetSpend(
        string SubscriptionId,
        string SubscriptionName,
        bool Success,
        double Cost,
        string? Currency,
        string? BudgetName);

    internal sealed record CostScopeResult(
        string SubscriptionId,
        string SubscriptionName,
        int Status,
        double? Cost,
        string? Currency,
        string? Error);

    internal sealed record CostRow(string? Group, double Cost, string Currency);

    private sealed record AggregateCostResult(
        Dictionary<string, CostScopeResult> Results,
        string? Error);

    /// <summary>
    /// Returns null if the path is acceptable, otherwise a ready-to-return HTTP 400 message explaining
    /// the missing {scope} prefix. Scope-required providers (Cost Management, Consumption budgets,
    /// PolicyInsights states, etc.) MUST be prefixed with one of the five canonical scope shapes.
    /// Bare /providers/Microsoft.CostManagement/query was 5/27 of all 4xx failures in the last 5 days.
    /// </summary>
    private static string? ValidateScopePrefix(string path)
    {
        // Strip query string for the check
        var qIdx = path.IndexOf('?');
        var clean = qIdx >= 0 ? path[..qIdx] : path;

        // Only enforce on the providers that actually require {scope}. Cost Management is the big one.
        // Consumption/budgets and PolicyInsights/policyStates also require it. ResourceGraph, Capacity,
        // BillingBenefits, Billing, Advisor, etc. live at root and are unaffected.
        string[] scopeRequired =
        [
            "/providers/Microsoft.CostManagement/",
            "/providers/Microsoft.Consumption/budgets",
            "/providers/Microsoft.PolicyInsights/policyStates",
        ];

        foreach (var marker in scopeRequired)
        {
            if (!clean.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
            // Acceptable: the marker is preceded by a valid scope segment.
            if (clean.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("/providers/Microsoft.Management/managementGroups/", StringComparison.OrdinalIgnoreCase)
                || clean.StartsWith("/providers/Microsoft.Billing/billingAccounts/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return "HTTP 400 BadRequest\n" +
                   $"Path is missing the required {{scope}} prefix before '{marker.TrimEnd('/')}'.\n" +
                   "Prepend exactly ONE of:\n" +
                   "  /subscriptions/{subId}\n" +
                   "  /subscriptions/{subId}/resourceGroups/{rgName}\n" +
                   "  /providers/Microsoft.Management/managementGroups/{mgId}\n" +
                   "  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}\n" +
                   "  /providers/Microsoft.Billing/billingAccounts/{billingAccountId}/billingProfiles/{profileId}\n" +
                   $"Example: POST /subscriptions/{{subscriptionId}}/providers/Microsoft.CostManagement/query?api-version={AzureApiVersions.CostQuery}";
        }
        return null;
    }

    private static string? ValidateReadOnlyPostPath(string path, Activity? activity)
    {
        if (path.Contains('\\')
            || path.Contains('#')
            || Regex.IsMatch(path, "%2f|%5c|%23", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return BlockMutatingPost(activity);

        var qIdx = path.IndexOf('?');
        var clean = (qIdx >= 0 ? path[..qIdx] : path).TrimEnd('/');
        const string subscriptionScope = @"/subscriptions/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?:/resourceGroups/[^/]+)?";
        const string managementGroupScope = @"/providers/Microsoft\.Management/managementGroups/[^/]+";
        const string billingScope = @"/providers/Microsoft\.Billing/billingAccounts/[^/]+(?:/billingProfiles/[^/]+)?(?:/invoiceSections/[^/]+)?";
        var scopedCostManagement = $@"^(?:{subscriptionScope}|{managementGroupScope}|{billingScope})/providers/Microsoft\.CostManagement/(?:query|forecast|generateCostDetailsReport|generateReservationDetailsReport|pricesheets/default/download)$";
        string[] allowedPatterns =
        [
            scopedCostManagement,
            @"^/providers/Microsoft\.ResourceGraph/resources$",
            @"^/providers/Microsoft\.Capacity/(?:calculatePrice|calculateExchange)$",
            @"^/providers/Microsoft\.BillingBenefits/(?:calculatePrice|validatePurchase)$",
            @"^/providers/Microsoft\.Carbon/carbonEmissionReports$",
            @"^/providers/Microsoft\.Management/getEntities$",
            $@"^{subscriptionScope}/providers/Microsoft\.Advisor/recommendations/summarize$",
        ];
        if (allowedPatterns.Any(pattern => Regex.IsMatch(
                clean,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            return null;

        return BlockMutatingPost(activity);
    }

    private static string BlockMutatingPost(Activity? activity)
    {
        activity?.SetTag("azure.result", "blocked_mutating_post");
        activity?.SetStatus(ActivityStatusCode.Error, "Mutating POST blocked");
        return "HTTP 403 Forbidden\nThis agent only performs allowlisted read-only Azure POST operations. Mutating actions such as start, restart, deallocate, power off, or return are blocked.";
    }

    private async Task<string> BulkAzureRequest(
        [Description("JSON array of {method,path} objects, e.g. [{\"method\":\"GET\",\"path\":\"/subscriptions/.../providers/Microsoft.Compute/virtualMachines/vm1?api-version=2024-07-01\"}]")] string requestsJson,
        [Description("Max parallel requests in flight. Default 20, max 50.")] int parallelism = 20,
        [Description("Stop the whole bulk run on the first failure. Default false (continue and report all failures).")] bool stopOnFirstError = false)
    {
        using var activity = HttpHelper.Telemetry.StartActivity("BulkAzureRequest");
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", activity, "bulk");

        if (string.IsNullOrWhiteSpace(requestsJson))
            return "HTTP 400 BadRequest\nrequestsJson is empty.";

        List<BulkRequestItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<BulkRequestItem>>(
                requestsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return $"HTTP 400 BadRequest\nInvalid requestsJson: {ex.Message}";
        }
        if (items is null || items.Count == 0)
            return "HTTP 400 BadRequest\nrequestsJson must be a non-empty JSON array.";

        var maxPar = Math.Clamp(parallelism, 1, 50);
        activity?.SetTag("bulk.total", items.Count);
        activity?.SetTag("bulk.parallelism", maxPar);

        var sw = Stopwatch.StartNew();
        var results = new BulkResult[items.Count];
        var cts = new CancellationTokenSource();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maxPar, CancellationToken = cts.Token },
            async (i, ct) =>
            {
                var item = items[i];
                var (httpMethod, methodError) = HttpHelper.ResolveMethod(item.Method, activity, "bulk", allowReadOnlyPost: true);
                if (methodError is not null)
                {
                    results[i] = new BulkResult(i, 0, item.Path ?? "", methodError, false);
                    if (stopOnFirstError) cts.Cancel();
                    return;
                }
                if (string.IsNullOrWhiteSpace(item.Path) || !item.Path.StartsWith('/'))
                {
                    results[i] = new BulkResult(i, 400, item.Path ?? "", $"Invalid path: '{item.Path}'", false);
                    if (stopOnFirstError) cts.Cancel();
                    return;
                }
                if (httpMethod == HttpMethod.Post)
                {
                    var postError = ValidateReadOnlyPostPath(item.Path, activity);
                    if (postError is not null)
                    {
                        results[i] = new BulkResult(i, 403, item.Path, postError, false);
                        if (stopOnFirstError) cts.Cancel();
                        return;
                    }
                }

                var hasBody = !string.IsNullOrWhiteSpace(item.Body);
                var resp = await HttpHelper.SendWithRetryAsync(
                    $"https://management.azure.com{item.Path}",
                    token, activity, "bulk",
                    method: httpMethod,
                    jsonBody: hasBody && httpMethod != HttpMethod.Get ? item.Body : null,
                    includeTimestamp: false,
                    maxResponseChars: 1024); // hard cap so a stray verbose 4xx doesn't blow up the summary

                // Parse the "HTTP {code} {reason}\n{body}" envelope SendWithRetryAsync returns.
                var firstLine = resp.IndexOf('\n');
                var statusLine = firstLine > 0 ? resp[..firstLine] : resp;
                var statusParts = statusLine.Split(' ', 3);
                int.TryParse(statusParts.ElementAtOrDefault(1), out var status);
                var ok = status >= 200 && status < 300;
                var bodyPart = firstLine > 0 ? resp[(firstLine + 1)..] : "";

                string? name = null;
                try
                {
                    var idx = bodyPart.IndexOf('{');
                    if (idx >= 0)
                    {
                        using var doc = JsonDocument.Parse(bodyPart[idx..]);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("name", out var n))
                            name = n.GetString();
                    }
                }
                catch { /* ignore */ }

                results[i] = new BulkResult(i, status, item.Path, ok ? null : bodyPart, ok, name);
                if (!ok && stopOnFirstError) cts.Cancel();
            });

        sw.Stop();
        var succeeded = results.Count(r => r is not null && r.Ok);
        var failed = results.Count(r => r is not null && !r.Ok);
        activity?.SetTag("bulk.succeeded", succeeded);
        activity?.SetTag("bulk.failed", failed);
        activity?.SetTag("bulk.duration_ms", sw.ElapsedMilliseconds);

        var failuresPayload = results
            .Where(r => r is not null && !r.Ok)
            .Take(20)
            .Select(r => new
            {
                index = r!.Index,
                status = r.Status,
                path = r.Path,
                error = (r.Error ?? "").Length > 200 ? r.Error![..200] : r.Error
            });

        var successSamples = results
            .Where(r => r is not null && r.Ok)
            .Take(5)
            .Select(r => new { path = r!.Path, name = r.Name });

        var summary = new
        {
            total = items.Count,
            succeeded,
            failed,
            durationMs = sw.ElapsedMilliseconds,
            stopped = cts.IsCancellationRequested && stopOnFirstError,
            failures = failuresPayload,
            successSamples
        };
        return JsonSerializer.Serialize(summary);
    }

    private sealed class BulkRequestItem
    {
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "";
        public string? Body { get; set; }
    }

    private sealed record BulkResult(int Index, int Status, string Path, string? Error, bool Ok, string? Name = null);
}
