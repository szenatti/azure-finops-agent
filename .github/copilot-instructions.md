<!-- last refreshed: 2026-09-08 -->

# Azure FinOps Agent — Copilot Instructions

## Purpose

Azure FinOps Agent is an open-source Azure sample and delivery accelerator. It combines conversational AI with Azure Cost Management, ARM, Resource Graph, Advisor, Microsoft Graph, Log Analytics, public pricing, file analysis, visualizations, remediation scripts, and scheduled jobs.

It is designed for customers to deploy into **their own tenant and subscription**. Never commit maintainer or customer tenant IDs, subscription IDs, resource IDs, generated resource names, app IDs, user principal names, email addresses, IP addresses, connection strings, or deployment credentials.

This applies to all code, tests, documentation, examples, runbooks, and copied logs that can be committed. Use placeholders or runtime prompts for deployment targets and generate synthetic identifiers in tests. Never copy real Azure names, GUIDs, identifiers, or personal information from a conversation into these files. Check the staged diff for such data before committing or pushing.

## Stack

- Backend: .NET 10 minimal API in `src/Dashboard`
- Frontend: Vue 3 + Vite + ECharts in `src/Dashboard/frontend`
- Agent runtime: GitHub Copilot SDK with Azure OpenAI BYOK
- Authentication: anonymous chat plus optional multi-tenant Entra OAuth
- Hosting: Linux container on Azure App Service
- Infrastructure: `azure.yaml` + Bicep under `infra`
- Observability: OpenTelemetry + Application Insights

The SDK and bundled Copilot CLI are one compatibility unit. Let the installed `GitHub.Copilot.SDK` package supply `CopilotCliVersion`; do not retain an override from an older SDK. Before accepting an SDK bump, verify its exact platform CLI packages are published. A stale runtime can pass text/tool tests while breaking protocol features such as native image attachments.

## Core architecture

- A shared `CopilotClient` manages per-user `CopilotSession` instances.
- Session state is persisted under `COPILOT_HOME`; Entra users are isolated by OID and anonymous users by generated user ID.
- One `SemaphoreSlim` gate per user serializes session create/resume/replay. Do not bypass it: warmup and transcript replay otherwise race into `Session ... is already tracked`.
- One active turn per session is enforced by `ChatEndpoints`; scheduled jobs use the same turn gate.
- SSE streams deltas, reasoning, timing, tools, charts, generated files, scores, cooldowns, busy/errors, and completion.
- The backend continues a turn after browser disconnect and persists the answer. The frontend reconciles against the server turn gate and transcript.
- OAuth access tokens stay in memory. Only the encrypted refresh-token identity record is persisted.
- The Azure OpenAI provider uses `BearerTokenProvider`; keep token refresh callback-based rather than baking a static token into sessions.

## Security invariants

The agent can read and apply approved non-destructive changes, but it never deletes Azure resources.

- `DELETE` is blocked centrally for Azure and Graph pass-through tools.
- Azure `POST` is restricted to the read-only allowlist in `AzureQueryTools`; action endpoints such as start, restart, deallocate, power-off, and reservation return are blocked.
- `PUT` and `PATCH` remain available under the signed-in user's Azure RBAC.
- Destructive recommendations must use `GenerateScript` so the user reviews and runs them.
- Every session, job, upload, generated artifact, and transcript endpoint must enforce per-user ownership.
- Ownership of a session is established by comparing its recorded working directory to the caller's own. A filter passed to an SDK list call is a query hint, never the boundary: verify what comes back, drop entries with no recorded directory, and never adopt or resume a session that has not passed that check.
- Standard add-on consent tiers are read-only. Graph writes require separately granted write scopes.
- Never log or return bearer tokens, refresh tokens, secrets, authorization headers, or connection strings.

### Untrusted-input rules

Treat every model-authored tool argument as attacker-controlled: it is shaped by uploaded file contents, fetched pages, Azure resource names, and tags.

- A file path, blob name, URL, or command fragment must never come from a tool argument. The host resolves it from a per-user registry keyed by an opaque id.
- When a free-form JSON parameter bag is merged into a host-built request, reject host-owned keys explicitly. Merging the model's object last silently lets it override them.
- Filesystem readers take a base directory from the host, resolve the candidate with `realpath`, and require containment. Fail closed when the root is absent; an existence check is not a containment check.
- Error paths must not echo the requested path or resource id back to the model — that is a probing oracle.
- Anything reaching `v-html` is entity-escaped first, at the single entry point. Escaping only some subfragments (table cells, attributes) is not enough.
- Model-supplied chart/render options are allow-listed and stripped of DOM and HTML sinks before they reach a renderer.

## Authentication

OAuth tiers are resource-specific and delegated:

| Tier           | Resource               | Scopes                                                   |
| -------------- | ---------------------- | -------------------------------------------------------- |
| `base`         | Azure Resource Manager | `user_impersonation`                                     |
| `licenses`     | Microsoft Graph        | `User.Read`, `Organization.Read.All`, `Reports.Read.All` |
| `chargeback`   | Microsoft Graph        | `User.Read`, `User.Read.All`, `Group.Read.All`           |
| `loganalytics` | Log Analytics          | `Data.Read`                                              |
| `storage`      | Azure Storage          | `user_impersonation`                                     |

`tier=all` walks only the remaining add-on tiers through separate user-scoped consent screens. Do not combine cross-resource scopes or replace this with tenant-wide admin consent.

Disconnect/revoke/logout differ intentionally:

- Disconnect clears live/session tokens and the browser identity cookie, but retains the encrypted identity record for explicit reconnect.
- Revoke clears the cookie and encrypted identity record and forces fresh consent next time.
- Logout clears Entra identity and immediately assigns a new anonymous chat identity.

Before manually testing a fresh consent flow, revoke existing grants for the test app in the selected test tenant. Use placeholders or local configuration—never commit real values.

## Tool patterns

- Tools fetch data and return compact raw API JSON unless a bounded projection is explicitly required for performance.
- XLSX `workbook` inspection returns every sheet's shape, columns, and bounded numeric summaries in one call; reuse it instead of making a second aggregate call when the requested metric is already present.
- Prefer string parameters; SDK coercion of numeric arguments can be unreliable.
- Reuse one `CosmosClient`/HTTP client/session where applicable; do not create clients per request.
- Tools generally do not catch API exceptions internally. Handle failures at system boundaries and let telemetry capture dependency failures.
- Push aggregation, filtering, grouping, and limits into the source API.
- Parallelize independent calls, except Cost Management `/query` and `/forecast`, which are tenant-throttled.
- Never issue multiple Cost Management query calls in parallel. After a final 429, stop querying that service for the turn.
- Cost query/forecast requests share a tenant-keyed semaphore, one-second spacing, and cooldown across users, turns, and scheduled jobs in the same process. Tenant claims are used only for throttle bucketing, never authorization. Honor the longest positive standard or Cost Management/Consumption retry hint; return long cooldowns instead of shortening them. Multi-instance hosting needs distributed rate/cooldown coordination.
- Every 429 includes a `finopsRetry` JSON object with retry time, delay source, and Azure-versus-local-cooldown source. Do not rely on text before the JSON body: the execution panel's JSON formatter omits it. Headerless cost-query throttles use a labelled 60-second fallback and no rapid retry; this is not a guarantee of quota availability.
- Azure 429 results include bounded, explicitly allow-listed `finopsRetry.rateLimitHeaders` for diagnosing the exhausted quota. Never copy arbitrary response headers, authorization, or cookies into diagnostics. Local cooldowns have no new server headers.
- Successful cost-query responses are cached for five minutes under hashed caller-token + request keys, never across principals. Preserve fetch-time guidance and the HTTP-status-line + JSON-body contract.
- Scope discovery follows subscription and management-group pages with same-host/path HTTPS continuation validation, a five-minute caller-token-isolated cache, and explicit incomplete flags. Use `FindSubscriptions` for bounded name/id resolution, never shell parsing of ARM inventory. Cache/prompt bounds are not proof that all scopes were discovered.

### Cross-subscription cost

Use `QueryCostsAcrossSubscriptions` exactly once for all-subscription totals.

- Pass `subscriptionsJson='all'` to resolve all accessible scopes server-side; explicit arrays select a subset. Never treat a truncated connection-context array as the entire estate.
- It tries one supplied, verified containing management-group query, then at most 20 sequential subscription queries. Larger estates need a supported aggregate scope or Cost Management exports. Paginated cost responses cannot be accepted as complete totals.
- Results preserve complete/failed/unattempted counts with at most 50 detail rows. Partial data never produces a complete estate total.
- Budget `currentSpend` is the last evaluated cost, not live Cost Analysis data and not a service breakdown. Never substitute it for authoritative costs; empty budgets do not mean zero spend.
- Do not list subscriptions again; reuse connection metadata or the host discovery cache.

### Crawl maturity

Use `GetCrawlMaturityEvidence` exactly once for explicit Crawl scoring.

- It runs budget/current-spend, required-tag, exports, alert/scheduled-action, policy, common-waste, and empty-resource-group checks concurrently.
- Pass `subscriptionsJson='all'` for host-side discovery; explicit arrays select a subset. Do not copy a large connection-context array. The legacy management-group argument is not a scope filter or an inherited-policy audit.
- Evidence reads have a shared 30-second deadline after discovery, a 12-request concurrency cap, and interleaved collection categories. Queued and in-flight work must respect cancellation. Successful reads cache for five minutes under caller-token/request hashes; incomplete results are explicitly provisional, never proof of absent controls.
- Compute scores using all collected evidence, but return aggregate counts and at most three samples per category. Preserve the `kind`, `scores`, and `followUp` SSE contract. Never send the full per-subscription evidence to the model or ask it to shell-parse Crawl results.
- It computes and persists all seven scores and returns follow-up actions.
- Each score carries `evidenceComplete`; the top-level `complete` flag describes collection completeness. Policy evidence remains a metadata keyword scan, not a definition/effect audit. Empty resource groups are hygiene findings, not billable resources or quantified savings.
- Budget-based spend evidence is explicitly labeled last evaluated, with coverage and unknown evaluation time; it is not live MTD cost.
- `ChatEndpoints` emits `maturity_score` and `follow_up` directly.
- Do not call `QueryAzure`, `FindIdleResources`, `ReportMaturityScore`, or `SuggestFollowUp` in the same Crawl turn.
- Walk, Run, and Playbook continue to use `ReportMaturityScore`.

### Retail pricing

- One filter combination: one `GetAzureRetailPricing` call.
- Two or more independent combinations: one `GetAzureRetailPricingBatch` call.
- One SKU across regions uses one comma-separated region request.
- Reuse returned rows; do not invoke shell tools to reparse usable pricing results.
- The tool holds no per-SKU domain knowledge. Every response carries a `FACETS` block of live distinct field values, and rows are grouped by `meterName`, cheapest-first within each meter.
- Meter, product and SKU names are not derivable from the ARM SKU (`Standard_ND96asr_v4` meters as `ND96asr_A100_v4`). When such a filter matches zero rows the tool drops it, re-queries on the structural filter alone, and says so — it must never return an empty table.
- `priceType='Consumption'` includes Spot and Low Priority. Comparisons stay within one `meterName`, and answers default to the ordinary on-demand meter unless another variant was requested.
- Foundry model comparisons must use the intended deployment tier/zone and must not silently choose Batch, cached, or Data Zone rows when Standard Global was requested.

### Charts and generated files

- One response contains one chart or one table, not both.
- Generated script/deck markers are converted into structured SSE events.
- Download endpoints require an authenticated session and owner match.
- Expired artifacts render an expired state rather than a dead link.

## Scheduled jobs

- Jobs are Entra-only and use delegated refresh tokens.
- Ownership is exact OID match; never fall back to a derived user-ID match.
- Limits: 3 active jobs per user; custom cadence 1–43200 minutes; sub-daily expiry 7 days; daily or slower expiry 90 days; 5 consecutive failures auto-pause.
- Resume is cap-checked exactly like create.
- Every job owns one dedicated run-log session; it is hidden from Conversations while the job exists and reappears when the job is deleted.
- Run logs have no composer. They expose Build deck, Summarize runs, and Edit job.
- Create+run-now, explicit run-now, and edit+run-now all call `watchJobRunAndOpen`.
- Templates: capacity check, reserve when available, 1-minute test, daily digest, anomaly watch, budget guard, idle sweep, Advisor watch, and retry last question.

## Frontend invariants

- At 900px and below, the left navigation is an overlay and the right execution sidebar is hidden.
- Auto-scroll follows only while near the bottom. User scroll-up must never be overridden.
- Hidden browser tabs suspend ResizeObserver, animation frames, transitions, and smooth scrolling. Keep reactive watcher fallbacks.
- Do not rewrite punctuation in streamed model text. Identifiers such as hostnames, versions, and Azure resource names must remain byte-for-byte intact.
- Escape all model/tool-influenced text before `v-html` transformations.
- Only the explicit Stop action marks a response as stopped; an arbitrary `AbortError` is recoverable transport failure.
- Attachment callbacks must update chips by stable `uid`, never by array index. Wait for uploads before sending, delist files whose chips were removed in flight, and revoke blob thumbnail URLs only after Vue unmounts them.
- Generated HTML previews must stay in a sandboxed iframe without `allow-same-origin`; model-produced deck scripts must never inherit access to application cookies, storage, DOM, or authenticated APIs.

## Code conventions

- Follow Microsoft C# conventions and modern Vue Composition API patterns.
- Preserve public APIs unless the task requires a change.
- Keep API endpoints RESTful and ownership-checked.
- Use current stable Azure API versions. Document intentional older versions where a service has not adopted the newest family version.
- Prefer managed identity and OIDC over client secrets.
- Keep unrelated formatting out of functional changes.
- Update `CHANGELOG.md` and this file whenever architecture, tools, security boundaries, dependencies, or project structure change.

## Local development

Secrets use .NET User Secrets; never commit local settings.

```powershell
cd src/Dashboard/frontend
npm ci
npm run build

cd ..
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --urls http://localhost:5000
```

The frontend must be built before backend startup so `wwwroot` exists when ASP.NET resolves `WebRootPath`.

## Testing

- Backend: `dotnet build src/Dashboard/Dashboard.csproj --no-restore`
- Large-tenant regression checks (no Azure calls): `dotnet run --project tests/LargeTenant.RegressionTests -p:CopilotSkipCliDownload=true`. Uses the actual Dashboard assembly with fake HTTP responses; covers retries, structured cooldown reporting, caching isolation, pagination, compact lookup, incomplete cost results, and bounded/cancellable Crawl assessments with 227 subscriptions.
- Frontend: `npm run build` under `src/Dashboard/frontend`
- Always verify the rendered UI for UI changes; a successful build is not a browser test.
- Measure latency from the app's SSE stream, not rendered pixels.
- Before every send, wait for the composer to be enabled and for the Stop button to be absent.
- Select Stop only by `.action-btn--stop`.
- For authenticated UI runs, pin the exact tab the user shared and use `run_playwright_code` exclusively. Do not use high-level browser helpers or the separate `mcp_playwright_browser_*` surface. Never open or navigate a replacement tab; if shared access is lost, ask the user to re-share the same signed-in tab.
- Check console errors, page errors, failed requests, tool sequence/count, TTFT, total time, and persisted transcript.
- After edits, verify disk state with `git status --short`; save all editor buffers before building.

## Deployment

Customer deployment uses `azd up` and generates names from the selected environment. Never put deployment coordinates in tracked files.

Maintainer CI workflows read deployment settings from GitHub repository variables and secrets:

- Production variables: `PROD_ACR_NAME`, `PROD_ACR_LOGIN_SERVER`, `PROD_CONTAINER_IMAGE`, `PROD_WEBAPP_NAME`, `PROD_RESOURCE_GROUP`, `PROD_VERIFY_URL`
- Test variables: corresponding `TEST_*` names plus `TEST_SLOT_NAME`
- OIDC secrets: `AZURE_*` for test and `AZURE_PROD_*` for production

Production OIDC must be branch-scoped to `main` and least-privileged: `AcrPush` on the target registry and `Website Contributor` on the target web app. App Service pulls images with its own managed identity and `AcrPull`.

Do not deploy without explicit user instruction. When instructed, validate builds, diff, secrets, account context, workflow configuration, and target version before pushing.

## Observability

The collector caps trace resource, span, and span-event string attributes at 4096 before Azure Monitor export, below its 8192-character property limit. This affects telemetry copies only, not model input, tool results, or persisted chat. Numeric fields and span identity/timing/status remain intact. Truncated JSON attributes are diagnostic excerpts, not complete documents; truncation is not secret redaction. Keep the transform before batching and validate it with the collector release pinned in the Dockerfile.

The image build runs `otelcol validate` against the exact Linux collector and configuration it packages, using a generated test instrumentation key and loopback endpoint. This checks configuration without starting the collector or contacting Azure. Do not bypass this build gate or supply real credentials to it.

Discover Application Insights and Log Analytics identifiers from `azd env get-values`, Azure Resource Graph, or the deployed resource group. Never hardcode an application ID, workspace ID, subscription, or resource group in prompts or instructions.

For workspace-based Application Insights, query the Log Analytics `AppExceptions`, `AppTraces`, `AppRequests`, and `AppDependencies` tables. Confirm telemetry pipeline activity before interpreting an empty exception result.
