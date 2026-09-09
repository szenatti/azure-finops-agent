using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureFinOps.Dashboard.Auth;
using AzureFinOps.Dashboard.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;

namespace AzureFinOps.Dashboard.AI.Tools;

/// <summary>
/// Collects the seven Crawl-level FinOps evidence sets in one agent tool call.
/// The underlying ARM reads still fan out, but they do so server-side instead
/// of forcing one model round-trip per API batch.
/// </summary>
public sealed class CrawlMaturityTools
{
    private readonly UserTokens _tokens;
    private readonly Action<string, string> _saveScore;
    private readonly HttpClient? _http;
    private readonly TimeSpan _evidenceTimeout;
    private static readonly MemoryCache EvidenceCache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });

    public CrawlMaturityTools(UserTokens tokens, ScoreTools scoreTools)
        : this(tokens, scoreTools.SaveScore, null, TimeSpan.FromSeconds(30))
    {
    }

    internal CrawlMaturityTools(UserTokens tokens, Action<string, string> saveScore, HttpClient? http, TimeSpan evidenceTimeout)
    {
        _tokens = tokens;
        _saveScore = saveScore;
        _http = http;
        _evidenceTimeout = evidenceTimeout;
    }

    public IEnumerable<AIFunction> Create()
    {
        yield return AIFunctionFactory.Create(GetCrawlMaturityEvidence, "GetCrawlMaturityEvidence", @"Collects and scores all seven Crawl maturity dimensions in ONE tool call: budgets/last evaluated spend, exact CostCenter/Owner/Environment tagging, exports, alerts/scheduled actions, policy guardrails, common waste, and cost visibility. Only complete assessments enter score history. It also returns ready-to-render fix actions. Low-cost metadata reads run with bounded server-side concurrency. Budget currentSpend is the last budget evaluation, not live Cost Analysis data; report it only as last evaluated spend with coverage, never as an authoritative live MTD total.
    Use exactly once for Crawl/FinOps maturity scoring. Pass subscriptionsJson='all' for host discovery and automatic current-state selection (Enabled, Warned, PastDue), or an explicit JSON array for a user-selected subset. State exclusions are counted in scopeSelection and prevent an estate-complete claim; they do not imply zero historical costs or absent resources. Never copy a long context array or use shell tools. Evidence reads have a 30-second budget and reuse caller-isolated successful reads for five minutes. Results are compact with coverage and provisional scores for incomplete evidence; partial scores are not proof that controls are missing. Do NOT supplement it with QueryAzure, ReportMaturityScore, SuggestFollowUp, shell, file-reading, or any other tool—the score persistence, maturity SSE event, and follow-up buttons are already handled by this result. Render the supplied scores and stop.");
    }

    private async Task<string> GetCrawlMaturityEvidence(
        [Description("Use 'all' for all accessible subscriptions, or an explicit JSON array of selected scopes with id/name fields")] string subscriptionsJson,
        [Description("Legacy context hint only; does not filter subscriptions or audit inherited policies. Use explicit subscription scopes to narrow the assessment.")] string? managementGroupId = null)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var token = _tokens.AzureToken;
        if (string.IsNullOrEmpty(token))
            return HttpHelper.TokenMissing("AzureToken", null, "crawl");

        var excludedScopes = 0;
        object scopeSelection = new { mode = "explicit", excluded = 0 };
        if (subscriptionsJson.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var discovery = await AzureScopeDiscovery.SubscriptionsAsync(token);
            if (!discovery.Complete)
                return JsonSerializer.Serialize(new { complete = false, source = "scopeDiscovery", error = discovery.Error,
                    guidance = "Subscription discovery is incomplete. No maturity score was calculated; retry discovery later." });
            var selected = discovery.Scopes.Where(scope =>
                string.Equals(scope.State, "Enabled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope.State, "Warned", StringComparison.OrdinalIgnoreCase)
                || string.Equals(scope.State, "PastDue", StringComparison.OrdinalIgnoreCase)).ToArray();
            excludedScopes = discovery.Scopes.Count - selected.Length;
            scopeSelection = new
            {
                mode = "currentState", discovered = discovery.Scopes.Count, selected = selected.Length,
                excluded = excludedScopes,
                excludedByState = discovery.Scopes.Except(selected).GroupBy(scope => scope.State ?? "Unknown")
                    .ToDictionary(group => group.Key, group => group.Count()),
                guidance = "Only Enabled, Warned and PastDue scopes are selected automatically. Other states are excluded, not proof of absent resources or historical costs. Use explicit scopes to assess an excluded readable subscription."
            };
            if (selected.Length == 0)
                return JsonSerializer.Serialize(new { complete = false, source = "scopeSelection", scopeSelection,
                    error = "No enabled, warned or past-due subscriptions were found; no score was calculated." });
            subscriptionsJson = JsonSerializer.Serialize(selected.Select(scope => new { id = scope.Id, name = scope.Name }));
        }
        var (subscriptions, parseError) = ParseSubscriptions(subscriptionsJson);
        if (parseError is not null) return $"HTTP 400 BadRequest\n{parseError}";
        if (subscriptions.Count == 0) return "HTTP 400 BadRequest\nNo valid subscription IDs were supplied.";

        var ids = subscriptions.Select(s => s.Id).ToArray();

        const string taggingQuery =
            "resources | extend ccValue=tolower(tostring(tags['CostCenter'])), ownerValue=tolower(tostring(tags['Owner'])), envValue=tolower(tostring(tags['Environment'])) | extend cc=iff(isnotempty(ccValue) and ccValue !in ('unassigned','unknown','n/a','none','tbd','-'),1,0), own=iff(isnotempty(ownerValue) and ownerValue !in ('unassigned','unknown','n/a','none','tbd','-'),1,0), env=iff(isnotempty(envValue) and envValue !in ('unassigned','unknown','n/a','none','tbd','-'),1,0), ccPlaceholder=iff(ccValue in ('unassigned','unknown','n/a','none','tbd','-'),1,0), ownPlaceholder=iff(ownerValue in ('unassigned','unknown','n/a','none','tbd','-'),1,0), envPlaceholder=iff(envValue in ('unassigned','unknown','n/a','none','tbd','-'),1,0), hasDeptLower=iff(isnotempty(tostring(tags['department'])),1,0), hasDeptUpper=iff(isnotempty(tostring(tags['Department'])),1,0) | extend governed=iff(cc==1 and own==1 and env==1,1,0) | summarize total=count(), costCenter=sum(cc), owner=sum(own), environment=sum(env), fullyTagged=sum(governed), placeholders=sum(ccPlaceholder)+sum(ownPlaceholder)+sum(envPlaceholder), departmentLower=sum(hasDeptLower), departmentUpper=sum(hasDeptUpper) by subscriptionId | project subscriptionId, total, costCenter, owner, environment, fullyTagged, placeholders, departmentLower, departmentUpper | order by total desc";
        const string policyQuery =
            "policyresources | where type =~ 'microsoft.authorization/policyassignments' | extend raw=tolower(tostring(properties)) | extend finops=raw has_any ('tag','cost','allowedlocations','allowedskus','budget') | summarize totalAssignments=count(), finOpsAssignments=countif(finops) by subscriptionId | project subscriptionId, totalAssignments, finOpsAssignments | order by totalAssignments desc";
        const string wasteQuery =
            "resources | extend wasteType=case(type =~ 'microsoft.compute/disks' and (isempty(tostring(managedBy)) or tostring(properties.diskState) =~ 'Unattached'),'Unattached disk', type =~ 'microsoft.network/publicipaddresses' and isempty(tostring(properties.ipConfiguration.id)) and isempty(tostring(properties.natGateway.id)),'Orphaned public IP', type =~ 'microsoft.web/serverfarms' and toint(properties.numberOfSites)==0 and tostring(sku.tier) !~ 'Free' and tostring(sku.tier) !~ 'Shared','Empty App Service plan','') | where wasteType != '' | summarize wasteCount=count(), names=make_set(name,10) by subscriptionId, wasteType";
        const string emptyResourceGroupsQuery =
            "resourcecontainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups' | project subscriptionId, resourceGroup=name | join kind=leftouter (resources | summarize resourceCount=count() by subscriptionId, resourceGroup) on subscriptionId, resourceGroup | extend resourceCount=coalesce(resourceCount,0) | where resourceCount==0 | summarize emptyGroupCount=count(), names=make_set(resourceGroup,10) by subscriptionId";

        using var requestLimiter = new SemaphoreSlim(12, 12);
        using var deadline = new CancellationTokenSource(_evidenceTimeout);
        var cancellationToken = deadline.Token;
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var currentMonthStart = new DateOnly(utcToday.Year, utcToday.Month, 1);

        var taggingTask = RunResourceGraph(token, ids, taggingQuery, "crawl.tagging", requestLimiter, cancellationToken);
        var policyTask = RunResourceGraph(token, ids, policyQuery, "crawl.policy", requestLimiter, cancellationToken);
        var wasteTask = RunResourceGraph(token, ids, wasteQuery, "crawl.waste", requestLimiter, cancellationToken);
        var emptyGroupsTask = RunResourceGraph(token, ids, emptyResourceGroupsQuery, "crawl.empty_groups", requestLimiter, cancellationToken);
        var collectionTask = Task.WhenAll(subscriptions.Select(async scope =>
        {
            var budget = ReadArmCollection(token,
                $"/subscriptions/{scope.Id}/providers/Microsoft.Consumption/budgets?api-version={AzureApiVersions.Budgets}",
                "crawl.budgets", requestLimiter, cancellationToken);
            var exports = ReadArmCollection(token,
                $"/subscriptions/{scope.Id}/providers/Microsoft.CostManagement/exports?api-version={AzureApiVersions.CostExports}",
                "crawl.exports", requestLimiter, cancellationToken);
            var actions = ReadArmCollection(token,
                $"/subscriptions/{scope.Id}/providers/Microsoft.CostManagement/scheduledActions?api-version={AzureApiVersions.ScheduledActions}",
                "crawl.scheduled_actions", requestLimiter, cancellationToken);
            var alerts = ReadArmCollection(token,
                $"/subscriptions/{scope.Id}/providers/Microsoft.CostManagement/alerts?api-version={AzureApiVersions.CostAlerts}",
                "crawl.alerts", requestLimiter, cancellationToken);
            await Task.WhenAll(budget, exports, actions, alerts);
            return (Budget: CompactBudget(scope, budget.Result, currentMonthStart, utcToday),
                Exports: CompactCollection(scope, exports.Result), Actions: CompactCollection(scope, actions.Result),
                Alerts: CompactCollection(scope, alerts.Result));
        }));

        await Task.WhenAll(taggingTask, policyTask, wasteTask, emptyGroupsTask, collectionTask);
        var apiMs = totalSw.ElapsedMilliseconds;

        var budgets = collectionTask.Result.Select(result => result.Budget).ToList();
        var exportsEvidence = collectionTask.Result.Select(result => result.Exports).ToArray();
        var actionsEvidence = collectionTask.Result.Select(result => result.Actions).ToArray();
        var alertsEvidence = collectionTask.Result.Select(result => result.Alerts).ToArray();
        var currencies = budgets
            .Where(b => b.CurrentSpend is not null)
            .Select(b => b.Currency)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var totalsByCurrency = budgets
            .Where(b => b.CurrentSpend is not null && !string.IsNullOrWhiteSpace(b.Currency))
            .GroupBy(b => b.Currency!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Math.Round(g.Sum(b => b.CurrentSpend!.Value), 6), StringComparer.OrdinalIgnoreCase);
        var mtdFromBudgets = currencies.Length == 1
            ? totalsByCurrency[currencies[0]]
            : (double?)null;
        // A total built from only some subscriptions must never be narrated as
        // the estate MTD, so publish the coverage alongside every spend field.
        var subscriptionsWithValidatedSpend = budgets.Count(b => b.CurrentSpend is not null);
        var spendComplete = subscriptionsWithValidatedSpend == subscriptions.Count;

        var evidence = new
        {
            generatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            subscriptionCount = subscriptions.Count,
            subscriptions,
            budgets = new
            {
                subscriptionsWithBudgets = budgets.Count(b => b.BudgetCount > 0),
                totalBudgets = budgets.Sum(b => b.BudgetCount),
                mtdCurrentSpend = mtdFromBudgets is null ? (double?)null : Math.Round(mtdFromBudgets.Value, 2),
                spendFreshness = "Last budget evaluation; not live Cost Analysis. Evaluation timestamp unavailable.",
                spendComplete,
                subscriptionsWithValidatedSpend,
                currency = currencies.Length == 1 ? currencies[0] : null,
                totalsByCurrency,
                details = budgets
            },
            tagging = taggingTask.Result,
            exports = exportsEvidence,
            scheduledActions = actionsEvidence,
            alerts = alertsEvidence,
            policy = policyTask.Result,
            waste = new
            {
                commonPatterns = wasteTask.Result,
                emptyResourceGroups = emptyGroupsTask.Result
            },
            visibility = new
            {
                mtdSource = "Microsoft.Consumption budgets currentSpend",
                spendFreshness = "Last budget evaluation; not live Cost Analysis. Evaluation timestamp unavailable.",
                mtdCurrentSpend = mtdFromBudgets is null ? (double?)null : Math.Round(mtdFromBudgets.Value, 2),
                spendComplete,
                currency = currencies.Length == 1 ? currencies[0] : null,
                totalsByCurrency,
                subscriptionsWithValidatedSpend,
                ownershipSignal = "CostCenter + Owner + Environment tag coverage"
            }
        };

        var scores = BuildScores(
            subscriptions,
            budgets,
            taggingTask.Result,
            exportsEvidence,
            actionsEvidence,
            alertsEvidence,
            policyTask.Result,
            wasteTask.Result,
            emptyGroupsTask.Result,
            mtdFromBudgets,
            currencies.Length == 1 ? currencies[0] : null,
            totalsByCurrency);
        var scoreJson = JsonSerializer.Serialize(scores);
        var complete = excludedScopes == 0 && scores.All(score => score.EvidenceComplete);
        if (complete) _saveScore("crawl", scoreJson);

        var emptyGroups = DataRows(emptyGroupsTask.Result);
        var emptyGroupCount = emptyGroups.Sum(r => IntProperty(r, "emptyGroupCount"));
        var emptyGroupNames = emptyGroups
            .SelectMany(r => StringArrayProperty(r, "names"))
            .Take(3)
            .Select(name => Truncate(name, 100))
            .ToArray();
        var firstActionPrompt = complete
            ? $"Review existing valid CostCenter, Owner, and Environment values, then generate one script that adds the missing tags, daily exports and anomaly alerts across {subscriptions.Count} subscriptions. Never invent tag values, and do not delete resources."
            : "Review the incomplete Crawl evidence categories and help me select a smaller subscription scope for a complete read-only assessment. Do not make changes.";
        var followUpActions = new[]
        {
            new { label = complete ? "Review tags + exports + alerts" : "Review incomplete Crawl checks", prompt = firstActionPrompt },
            new { label = "Re-score Crawl maturity", prompt = "Re-score my Crawl FinOps maturity across all connected subscriptions and compare it with the prior score." },
            new
            {
                label = $"Review {emptyGroupCount} empty resource groups",
                prompt = $"Review the {emptyGroupCount} empty resource groups, including {string.Join(", ", emptyGroupNames)}, and generate a dry-run cleanup script with confirmations; do not delete anything."
            }
        };
        var followUp = new
        {
            label = followUpActions[0].label,
            prompt = followUpActions[0].prompt,
            actions = followUpActions
        };

        return JsonSerializer.Serialize(new
        {
            kind = "crawl_maturity_result",
            complete,
            scopeSelection,
            historyWriteRequested = complete,
            guidance = complete ? "Answer from the compact evidence; no more tools are required."
                : "Provisional assessment: unread scopes are unknown, not missing controls. Use evidenceComplete on each score. Do not infer a complete estate rating or live cost total. No shell processing is required.",
            scores,
            followUp,
            evidence = SummarizeEvidence(evidence),
            diagnostics = new
            {
                apiMs,
                totalToolMs = totalSw.ElapsedMilliseconds,
                evidenceBudgetSeconds = _evidenceTimeout.TotalSeconds,
                deadlineReached = deadline.IsCancellationRequested,
                cachePolicy = "Successful evidence reads may be up to 5 minutes old; cached budget spend remains last evaluated, not live cost."
            }
        });
    }

    private async Task<string> SendArm(
        string token,
        string path,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter,
        CancellationToken cancellationToken)
    {
        var url = path.StartsWith("https://management.azure.com/", StringComparison.OrdinalIgnoreCase)
            ? path
            : $"https://management.azure.com{path}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase))
            return "HTTP 400 BadRequest\nInvalid ARM collection continuation URL.";

        return await SendEvidence(token, uri.AbsoluteUri, telemetryPrefix, requestLimiter, cancellationToken);
    }

    private async Task<string> SendEvidence(string token, string url, string telemetryPrefix,
        SemaphoreSlim requestLimiter, CancellationToken cancellationToken, string? body = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token, url, body }))));
        if (EvidenceCache.TryGetValue(key, out string? cached)) return cached!;
        var acquired = false;
        try
        {
            await requestLimiter.WaitAsync(cancellationToken);
            acquired = true;
            var response = await HttpHelper.SendCoreAsync(_http, url, token, null, telemetryPrefix,
                method: body is null ? HttpMethod.Get : HttpMethod.Post, jsonBody: body,
                bypassCostManagementGate: true,
                maxAttemptsOverride: 1, cancellationToken: cancellationToken);
            if (ParseStatus(response) == 200 && Encoding.UTF8.GetByteCount(response) <= 512 * 1024)
                EvidenceCache.Set(key, response, new MemoryCacheEntryOptions
                {
                    Size = Encoding.UTF8.GetByteCount(response), AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                });
            return response;
        }
        catch (OperationCanceledException)
        {
            return acquired ? "HTTP 408 RequestTimeout\nEvidence request exceeded the time budget; scope is unknown."
                : "HTTP 0 NotAttempted\nEvidence budget expired before the request started; scope is unknown.";
        }
        catch (HttpRequestException)
        {
            return "HTTP 0 TransportError\nEvidence request failed; scope is unknown.";
        }
        finally
        {
            if (acquired) requestLimiter.Release();
        }
    }

    private async Task<string> ReadArmCollection(
        string token,
        string initialPath,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter,
        CancellationToken cancellationToken)
    {
        const int maxPages = 20;
        const int maxItems = 5000;
        var values = new List<JsonElement>();
        var next = initialPath;
        var initialUri = new Uri($"https://management.azure.com{initialPath}");
        var visited = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < maxPages; page++)
        {
            if (!Uri.TryCreate(initialUri, next, out var nextUri)
                || !AzureScopeDiscovery.IsSafeContinuation(nextUri, initialUri.AbsolutePath)
                || !visited.Add(nextUri.AbsoluteUri))
                return "HTTP 0 InvalidContinuation\nEvidence pagination is incomplete.";
            var response = await SendArm(token, nextUri.AbsoluteUri, telemetryPrefix, requestLimiter, cancellationToken);
            if (ParseStatus(response) != 200) return response;

            try
            {
                using var doc = JsonDocument.Parse(ResponseBody(response));
                if (!doc.RootElement.TryGetProperty("value", out var pageValues)
                    || pageValues.ValueKind != JsonValueKind.Array)
                    return "HTTP 0 ParseError\nARM collection response did not contain a value array.";

                values.AddRange(pageValues.EnumerateArray().Select(item => item.Clone()));
                if (values.Count > maxItems)
                    return $"HTTP 206 PartialContent\nARM collection exceeded the {maxItems}-item safety limit.";

                next = doc.RootElement.TryGetProperty("nextLink", out var nextLink)
                    ? nextLink.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(next))
                {
                    var body = JsonSerializer.Serialize(new { value = values });
                    return $"HTTP 200 OK\n{body}";
                }
            }
            catch (JsonException ex)
            {
                return $"HTTP 0 ParseError\n{Truncate(ex.Message, 300)}";
            }
        }

        return $"HTTP 206 PartialContent\nARM collection exceeded the {maxPages}-page safety limit.";
    }

    private async Task<object> RunResourceGraph(
        string token,
        string[] subscriptions,
        string query,
        string telemetryPrefix,
        SemaphoreSlim requestLimiter,
        CancellationToken cancellationToken)
    {
        const int maxRows = 5000;
        var rows = new List<JsonElement>();
        string? skipToken = null;

        for (var page = 0; page < 10; page++)
        {
            var options = new Dictionary<string, object?>
            {
                ["resultFormat"] = "objectArray",
                ["$top"] = 1000
            };
            if (!string.IsNullOrWhiteSpace(skipToken)) options["$skipToken"] = skipToken;
            var body = JsonSerializer.Serialize(new { subscriptions, query, options });

            var response = await SendEvidence(token,
                "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2024-04-01",
                telemetryPrefix, requestLimiter, cancellationToken, body);

            var status = ParseStatus(response);
            if (status != 200)
                return new { status, error = Truncate(ResponseBody(response), 600) };

            try
            {
                using var doc = JsonDocument.Parse(ResponseBody(response));
                if (!doc.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    return new { status = 0, error = "Resource Graph response did not contain an objectArray data set." };

                rows.AddRange(data.EnumerateArray().Select(row => row.Clone()));
                if (rows.Count > maxRows)
                    return new { status = 206, error = $"Resource Graph evidence exceeded the {maxRows}-row safety limit." };

                skipToken = doc.RootElement.TryGetProperty("$skipToken", out var tokenElement)
                    ? tokenElement.GetString()
                    : doc.RootElement.TryGetProperty("skipToken", out tokenElement)
                        ? tokenElement.GetString()
                        : null;
                if (string.IsNullOrWhiteSpace(skipToken))
                {
                    if (doc.RootElement.TryGetProperty("resultTruncated", out var truncated)
                        && (truncated.ValueKind == JsonValueKind.True || truncated.ToString().Equals("true", StringComparison.OrdinalIgnoreCase)))
                        return new { status = 206, error = "Resource Graph truncated evidence without a continuation token; coverage is incomplete." };
                    var serializedRows = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(rows));
                    return new { status = 200, data = serializedRows };
                }
            }
            catch (JsonException ex)
            {
                return new { status = 0, parseError = ex.Message };
            }
        }

        return new { status = 206, error = "Resource Graph evidence exceeded the 10-page safety limit." };
    }

    private static BudgetEvidence CompactBudget(
        SubscriptionScope scope,
        string response,
        DateOnly periodStart,
        DateOnly periodEndInclusive)
    {
        var status = ParseStatus(response);
        if (status != 200)
            return new BudgetEvidence(scope.Id, scope.Name, status, 0, 0, null, null, 0, 0, Truncate(ResponseBody(response), 300));

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var values = doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : [];
            double amount = 0;
            var actualNotifications = 0;
            var forecastNotifications = 0;
            foreach (var item in values)
            {
                if (!item.TryGetProperty("properties", out var props)) continue;
                if (props.TryGetProperty("amount", out var amountEl) && amountEl.TryGetDouble(out var a)) amount += a;
                if (props.TryGetProperty("notifications", out var notifications)
                    && notifications.ValueKind == JsonValueKind.Object)
                {
                    foreach (var notification in notifications.EnumerateObject())
                    {
                        var n = notification.Value;
                        if (n.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) continue;
                        var thresholdType = n.TryGetProperty("thresholdType", out var type) ? type.GetString() : null;
                        if (string.Equals(thresholdType, "Forecasted", StringComparison.OrdinalIgnoreCase)) forecastNotifications++;
                        else actualNotifications++;
                    }
                }
            }
            var spend = AzureQueryTools.ReadUnfilteredBudgetSpend(
                (scope.Id, scope.Name),
                response,
                periodStart,
                periodEndInclusive);
            return new BudgetEvidence(
                scope.Id,
                scope.Name,
                status,
                values.Length,
                Math.Round(amount, 2),
                spend.Success ? Math.Round(spend.Cost, 6) : null,
                spend.Success ? spend.Currency : null,
                actualNotifications,
                forecastNotifications,
                null);
        }
        catch (JsonException ex)
        {
            return new BudgetEvidence(scope.Id, scope.Name, status, 0, 0, null, null, 0, 0, ex.Message);
        }
    }

    private static CollectionEvidence CompactCollection(SubscriptionScope scope, string response)
    {
        var status = ParseStatus(response);
        if (status != 200)
            return new CollectionEvidence(scope.Id, scope.Name, status, 0, [], Truncate(ResponseBody(response), 300));

        try
        {
            using var doc = JsonDocument.Parse(ResponseBody(response));
            var values = doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().ToArray()
                : [];
            var names = values
                .Select(v => v.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(10)
                .Cast<string>()
                .ToArray();
            return new CollectionEvidence(scope.Id, scope.Name, status, values.Length, names, null);
        }
        catch (JsonException ex)
        {
            return new CollectionEvidence(scope.Id, scope.Name, status, 0, [], ex.Message);
        }
    }

    internal static IReadOnlyList<MaturityScore> BuildScores(
        IReadOnlyList<SubscriptionScope> subscriptions,
        IReadOnlyList<BudgetEvidence> budgets,
        object taggingProjection,
        IReadOnlyList<CollectionEvidence> exports,
        IReadOnlyList<CollectionEvidence> scheduledActions,
        IReadOnlyList<CollectionEvidence> alerts,
        object policyProjection,
        object wasteProjection,
        object emptyGroupsProjection,
        double? mtdSpend,
        string? mtdCurrency,
        IReadOnlyDictionary<string, double> totalsByCurrency)
    {
        var tagRows = DataRows(taggingProjection);
        var totalResources = tagRows.Sum(r => IntProperty(r, "total"));
        var costCenter = tagRows.Sum(r => IntProperty(r, "costCenter"));
        var owner = tagRows.Sum(r => IntProperty(r, "owner"));
        var environment = tagRows.Sum(r => IntProperty(r, "environment"));
        var fullyTagged = tagRows.Sum(r => IntProperty(r, "fullyTagged"));
        var placeholderTags = tagRows.Sum(r => IntProperty(r, "placeholders"));
        var tagCoverage = totalResources == 0 ? 0 : fullyTagged * 100.0 / totalResources;
        var tagScore = totalResources == 0 ? 0 : tagCoverage switch
        {
            >= 95 => 5,
            >= 80 => 4,
            >= 60 => 3,
            >= 30 => 2,
            _ => 1
        };
        var tagSpread = string.Join(", ", subscriptions.OrderByDescending(scope => tagRows
            .Where(row => StringProperty(row, "subscriptionId").Equals(scope.Id, StringComparison.OrdinalIgnoreCase))
            .Sum(row => IntProperty(row, "total"))).Take(3).Select(s =>
        {
            var row = tagRows.FirstOrDefault(r =>
                StringProperty(r, "subscriptionId").Equals(s.Id, StringComparison.OrdinalIgnoreCase));
            var total = row.ValueKind == JsonValueKind.Undefined ? 0 : IntProperty(row, "total");
            var governed = row.ValueKind == JsonValueKind.Undefined ? 0 : IntProperty(row, "fullyTagged");
            var pct = total == 0 ? 0 : Math.Round(governed * 100.0 / total, 1);
            return $"{pct}% in {Truncate(s.Name, 100)}";
        }));

        var coveredBudgets = budgets.Count(b => b.Status == 200 && b.BudgetCount > 0);
        var allBudgetReadsOk = budgets.Count == subscriptions.Count && budgets.All(b => b.Status == 200);
        var allBudgetNotifications = budgets
            .Where(b => b.BudgetCount > 0)
            .All(b => b.EnabledActualNotifications > 0 && b.EnabledForecastNotifications > 0);
        var budgetScore = !allBudgetReadsOk ? 0
            : coveredBudgets == subscriptions.Count && allBudgetNotifications ? 5
            : coveredBudgets == subscriptions.Count ? 4
            : coveredBudgets > 0 ? 2
            : 1;
        var notificationCount = budgets.Sum(b => b.EnabledActualNotifications + b.EnabledForecastNotifications);

        var exportCount = exports.Sum(e => e.Count);
        var exportCoverage = exports.Count(e => e.Status == 200 && e.Count > 0);
        var exportsScore = exports.All(e => e.Status == 200)
            ? exportCoverage == subscriptions.Count ? 5 : exportCount > 0 ? 2 : 0
            : 0;

        var alertCount = alerts.Sum(a => a.Count);
        var actionCount = scheduledActions.Sum(a => a.Count);
        var alertCoverage = subscriptions.Count(s =>
            alerts.Any(a => a.SubscriptionId == s.Id && a.Count > 0)
            || scheduledActions.Any(a => a.SubscriptionId == s.Id && a.Count > 0));
        var alertsReadable = alerts.All(a => a.Status == 200) && scheduledActions.All(a => a.Status == 200);
        var alertsScore = !alertsReadable ? 0
            : alertCoverage == subscriptions.Count && alertCount + actionCount > 0 ? 5
            : alertCoverage > 0 ? 1
            : 0;

        var policyRows = DataRows(policyProjection);
        var totalPolicies = policyRows.Sum(r => IntProperty(r, "totalAssignments"));
        var finOpsPolicies = policyRows.Sum(r => IntProperty(r, "finOpsAssignments"));
        var policyCoverage = subscriptions.Count(s => policyRows.Any(r =>
            StringProperty(r, "subscriptionId").Equals(s.Id, StringComparison.OrdinalIgnoreCase)
            && IntProperty(r, "finOpsAssignments") > 0));
        var policyScore = ProjectionStatus(policyProjection) != 200 ? 0
            : policyCoverage == subscriptions.Count ? 5
            : policyCoverage * 2 >= subscriptions.Count ? 3
            : policyCoverage > 0 ? 2
            : 0;

        var wasteRows = DataRows(wasteProjection);
        var commonWaste = wasteRows.Sum(r => IntProperty(r, "wasteCount"));
        var emptyGroupRows = DataRows(emptyGroupsProjection);
        var emptyGroups = emptyGroupRows.Sum(r => IntProperty(r, "emptyGroupCount"));
        var totalWaste = commonWaste + emptyGroups;
        var wasteReadable = ProjectionStatus(wasteProjection) == 200 && ProjectionStatus(emptyGroupsProjection) == 200;
        var wasteScore = !wasteReadable ? 0 : totalWaste switch
        {
            0 => 5,
            1 => 4,
            <= 3 => 3,
            <= 10 => 2,
            _ => 1
        };
        var emptyGroupSpread = string.Join(", ", subscriptions.OrderByDescending(scope => emptyGroupRows
            .Where(row => StringProperty(row, "subscriptionId").Equals(scope.Id, StringComparison.OrdinalIgnoreCase))
            .Sum(row => IntProperty(row, "emptyGroupCount"))).Take(3).Select(s =>
        {
            var row = emptyGroupRows.FirstOrDefault(r =>
                StringProperty(r, "subscriptionId").Equals(s.Id, StringComparison.OrdinalIgnoreCase));
            var count = row.ValueKind == JsonValueKind.Undefined ? 0 : IntProperty(row, "emptyGroupCount");
            return $"{count} in {Truncate(s.Name, 100)}";
        }));

        var visibleSubscriptions = budgets.Count(b => b.Status == 200 && b.CurrentSpend is not null);
        var visibilityScore = visibleSubscriptions == 0 ? 0
            : visibleSubscriptions < subscriptions.Count ? 1
            : tagCoverage >= 80 ? 4
            : tagCoverage >= 30 ? 3
            : 2;
        var spendSpread = string.Join(", ", budgets
            .Where(b => b.CurrentSpend is not null)
            .OrderByDescending(b => b.CurrentSpend)
            .Take(3)
            .Select(b => $"{b.CurrentSpend!.Value.ToString("N2", CultureInfo.InvariantCulture)} {b.Currency ?? "currency unknown"} in {Truncate(b.SubscriptionName, 100)}"));
        var spendSummary = FormatCostSummary(mtdSpend, mtdCurrency, totalsByCurrency);

        MaturityScore[] scores =
        [
            new("budgets", "Budgets & thresholds", budgetScore,
                $"{coveredBudgets}/{subscriptions.Count} subscriptions have budgets; {budgets.Sum(b => b.BudgetCount)} budgets expose {notificationCount} enabled actual/forecast notifications. Last evaluated spend (not live Cost Analysis): {spendSummary}; coverage {visibleSubscriptions}/{subscriptions.Count}."),
            new("tagging", "Tagging for accountability", tagScore,
                $"Valid CostCenter+Owner+Environment coverage is {Math.Round(tagCoverage, 1)}% across {totalResources} resources (largest 3 scopes: {tagSpread}); exact valid-key counts are CostCenter={costCenter}, Owner={owner}, Environment={environment}, with {placeholderTags} placeholder values excluded."),
            new("exports", "Cost data exports", exportsScore,
                $"{exportCount} exports cover {exportCoverage}/{subscriptions.Count} subscriptions; {exports.Count(e => e.Status == 200)}/{subscriptions.Count} export-list calls succeeded."),
            new("alerts", "Cost alerts & scheduled actions", alertsScore,
                $"{alertCount} cost alerts and {actionCount} scheduled actions cover {alertCoverage}/{subscriptions.Count} subscriptions; {alerts.Count(a => a.Status == 200) + scheduledActions.Count(a => a.Status == 200)}/{subscriptions.Count * 2} list calls succeeded."),
            new("policy", "Governance guardrails", policyScore,
                $"Policy metadata keyword scan identified {finOpsPolicies} candidate FinOps assignments among {totalPolicies}, covering {policyCoverage}/{subscriptions.Count} subscriptions. This is not a full policy-definition or inherited-policy audit."),
            new("waste", "Waste identification & cleanup", wasteScore,
                $"{commonWaste} potential cost-waste resources and {emptyGroups} empty resource groups were found (largest 3 empty-group counts: {emptyGroupSpread}). Empty resource groups themselves have no resource charge; these are hygiene findings, not estimated savings."),
            new("visibility", "Cost visibility & ownership", visibilityScore,
                $"Last budget evaluation (not live Cost Analysis) provides {spendSummary} across {visibleSubscriptions}/{subscriptions.Count} subscriptions (top 3: {spendSpread}); governed ownership-tag coverage is {Math.Round(tagCoverage, 1)}% across {totalResources} resources.")
        ];
        return scores.Select(score =>
        {
            var evidenceComplete = score.Id switch
            {
                "budgets" => allBudgetReadsOk,
                "tagging" => ProjectionStatus(taggingProjection) == 200,
                "exports" => exports.All(item => item.Status == 200),
                "alerts" => alertsReadable,
                "policy" => ProjectionStatus(policyProjection) == 200,
                "waste" => wasteReadable,
                "visibility" => allBudgetReadsOk && ProjectionStatus(taggingProjection) == 200,
                _ => false
            };
            return score with
            {
                EvidenceComplete = evidenceComplete,
                Detail = evidenceComplete ? score.Detail : "Incomplete evidence; provisional score, unread scopes are unknown. " + score.Detail
            };
        }).ToArray();
    }

    private static string FormatCostSummary(
        double? total,
        string? currency,
        IReadOnlyDictionary<string, double> totalsByCurrency)
    {
        if (total is not null && !string.IsNullOrWhiteSpace(currency))
            return $"{total.Value.ToString("N2", CultureInfo.InvariantCulture)} {currency}";
        if (totalsByCurrency.Count > 0)
            return string.Join(" + ", totalsByCurrency
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Value.ToString("N2", CultureInfo.InvariantCulture)} {kvp.Key}"));
        return "no validated budget evaluation";
    }

    internal static object SummarizeEvidence(object evidence)
    {
        var root = JsonSerializer.SerializeToElement(evidence);
        var budgets = root.GetProperty("budgets");
        var summary = new Dictionary<string, object?>
        {
            ["generatedUtc"] = root.GetProperty("generatedUtc"),
            ["subscriptionCount"] = root.GetProperty("subscriptionCount"),
            ["detailPolicy"] = "Totals use all collected evidence; samples show at most 3 items per category. No shell processing is needed.",
            ["budgets"] = budgets.EnumerateObject().Where(property => property.Name != "details")
                .ToDictionary(property => property.Name, property => property.Value),
            ["budgetReads"] = SummarizeCollection(budgets.GetProperty("details")),
            ["visibility"] = root.GetProperty("visibility")
        };
        foreach (var name in new[] { "exports", "scheduledActions", "alerts" })
            summary[name] = SummarizeCollection(root.GetProperty(name));
        foreach (var name in new[] { "tagging", "policy" })
            summary[name] = SummarizeProjection(root.GetProperty(name));
        var waste = root.GetProperty("waste");
        summary["waste"] = new
        {
            commonPatterns = SummarizeProjection(waste.GetProperty("commonPatterns")),
            emptyResourceGroups = SummarizeProjection(waste.GetProperty("emptyResourceGroups"))
        };
        return summary;
    }

    private static object SummarizeCollection(JsonElement collection)
    {
        var rows = collection.EnumerateArray().ToArray();
        return new
        {
            scopes = rows.Length,
            succeeded = rows.Count(row => IntProperty(row, "Status") == 200),
            failed = rows.Count(row => IntProperty(row, "Status") != 200),
            notAttemptedOrTransportFailed = rows.Count(row => IntProperty(row, "Status") == 0),
            timedOut = rows.Count(row => IntProperty(row, "Status") == 408),
            statuses = rows.GroupBy(row => IntProperty(row, "Status")).ToDictionary(group => group.Key, group => group.Count()),
            itemCount = rows.Sum(row => IntProperty(row, "Count") + IntProperty(row, "BudgetCount")),
            failureSamples = rows.Where(row => IntProperty(row, "Status") != 200).Take(3)
                .Select(row => new { subscription = Truncate(StringProperty(row, "SubscriptionName"), 100), status = IntProperty(row, "Status") })
        };
    }

    private static object SummarizeProjection(JsonElement projection)
    {
        var rows = projection.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().ToArray() : [];
        var totals = new Dictionary<string, double>();
        foreach (var row in rows)
        foreach (var property in row.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number))
                totals[property.Name] = totals.GetValueOrDefault(property.Name) + number;
        }
        return new
        {
            status = IntProperty(projection, "status"),
            error = Truncate(StringProperty(projection, "error"), 200),
            rowCount = rows.Length,
            totals,
            samplesTruncated = rows.Length > 3,
            samples = rows.Take(3).Select(row => row.EnumerateObject().ToDictionary(property => property.Name,
                property => property.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)Truncate(property.Value.GetString() ?? "", 100),
                    JsonValueKind.Array => property.Value.EnumerateArray().Take(3)
                        .Select(value => Truncate(value.ToString(), 100)).ToArray(),
                    _ => property.Value
                }))
        };
    }

    private static List<JsonElement> DataRows(object projection)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(projection));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];
            return data.EnumerateArray().Select(r => r.Clone()).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int ProjectionStatus(object projection)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(projection));
            return doc.RootElement.TryGetProperty("status", out var status) && status.TryGetInt32(out var value)
                ? value
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static int IntProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static string StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
            ? value.GetString() ?? ""
            : "";

    private static IEnumerable<string> StringArrayProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .ToArray();
    }

    private static (List<SubscriptionScope> Subscriptions, string? Error) ParseSubscriptions(string json)
    {
        var scopes = new List<SubscriptionScope>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (scopes, "subscriptionsJson must be a JSON array.");
            if (doc.RootElement.GetArrayLength() > 10000)
                return (scopes, "subscriptionsJson supports at most 10000 entries; use a smaller explicit scope.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var rawId = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                rawId = rawId?.Trim();
                if (rawId?.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase) == true)
                    rawId = rawId["/subscriptions/".Length..].Trim('/');
                if (!Guid.TryParse(rawId, out var id)) continue;
                var name = item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var nameEl)
                    ? nameEl.GetString()
                    : null;
                var canonicalId = id.ToString();
                if (!seen.Add(canonicalId)) continue;
                scopes.Add(new SubscriptionScope(canonicalId, string.IsNullOrWhiteSpace(name) ? canonicalId : name!));
            }
            return (scopes, null);
        }
        catch (JsonException ex)
        {
            return (scopes, $"Invalid subscriptionsJson: {ex.Message}");
        }
    }

    private static int ParseStatus(string response)
    {
        var line = response.Split('\n', 2)[0];
        var parts = line.Split(' ', 3);
        return parts.Length > 1 && int.TryParse(parts[1], out var status) ? status : 0;
    }

    private static string ResponseBody(string response)
    {
        var newline = response.IndexOf('\n');
        return newline >= 0 ? response[(newline + 1)..] : "";
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];

    internal sealed record SubscriptionScope(string Id, string Name);

    internal sealed record BudgetEvidence(
        string SubscriptionId,
        string SubscriptionName,
        int Status,
        int BudgetCount,
        double ConfiguredBudgetAmountTotal,
        double? CurrentSpend,
        string? Currency,
        int EnabledActualNotifications,
        int EnabledForecastNotifications,
        string? Error);

    internal sealed record CollectionEvidence(
        string SubscriptionId,
        string SubscriptionName,
        int Status,
        int Count,
        string[] Names,
        string? Error);

    internal sealed record MaturityScore(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("score")] int Score,
        [property: JsonPropertyName("detail")] string Detail,
        [property: JsonPropertyName("evidenceComplete")] bool EvidenceComplete = true);
}
