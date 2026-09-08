# Large-Tenant Regression Checks

Run from the repository using the SDK pinned in `global.json`:

```sh
dotnet run --project tests/LargeTenant.RegressionTests -p:CopilotSkipCliDownload=true
```

This console harness references the actual Dashboard project without adding test package dependencies and exits nonzero on failure. It does not start the application, use credentials, or call Azure. HTTP responses are supplied by an in-memory handler; the cooldown guard is also tested against a deliberately unreachable host.

Coverage includes retry-header precedence and long waits, tenant isolation and cross-turn cooldown, request pacing, caller-isolated cost caching, a 230-subscription paginated inventory, bounded lookup and result summaries, unsafe continuation rejection, incomplete discovery, and the absence of the budget shortcut for authoritative cost totals.

Review regressions cover empty rows versus measured zero, caller-isolated inferred cooldowns, a warm cache hit under an occupied gate, faulted discovery recovery, cross-subscription cancellation, and the management-group fallback allowlist (400/403/404 only). Current-state Crawl retains Enabled/Warned/PastDue scopes and reports exclusions; historical cost queries retain every discovered state. Partial Crawl assessments never write history, and comparisons ignore explicitly incomplete historical entries while preserving legacy history. Session identity mismatches dispose the handle before registration; concurrent retry-reporter bind/dispose cannot resurrect a detached reporter or remove another registration's replacement.

HTTP response parsers consume only `HttpHelper` transport results: status line, newline, body. Successful composite cost/discovery/Crawl results are JSON documents; validation/authentication errors can retain their existing HTTP-prefixed messages. They must not be routed through transport parsers. The Crawl SSE handler parses success JSON directly; adding a status line would break that consumer. The suite parses composite JSON directly and separately checks transport envelope compatibility.

The Crawl checks exercise the actual tool with 227 synthetic subscriptions, assert a result below 24 KB, verify all seven score/aggregate counts, reject truncated Resource Graph evidence, and check caller-isolated cache reuse. A deliberately blocked HTTP handler verifies deadline cancellation of queued and active work without background requests continuing after the result. The helper tests also verify JSON-visible cooldown metadata and the conservative headerless-429 fallback.

Rate-limit diagnostics tests cover header attribution, unchanged longest-delay selection, bounded values, exclusion of authorization/cookie/unrelated headers, and the absence of invented response headers during local cooldowns.

Client-identification checks assert the fixed application-owned `ClientType` on public ARM query/forecast POSTs, reject overrides, and ensure unrelated methods/endpoints do not receive it. They verify that URI, body, caller token, method, and existing User-Agent are preserved without adding Portal headers. These are request-shape tests, not evidence that Azure will accept a previously throttled request.

Session callback checks suppress ambient execution-context flow, invoke two concurrent chats and an unwatched background job for the same synthetic owner, and verify exact-session cooldown routing. Model arguments cannot choose the owner/session. Wrappers preserve tool schemas/deferral metadata and restore caller activity after success, failure, and cancellation. These tests simulate the callback boundary; live Copilot SDK/SSE verification remains a separate deployment check.

Collector configuration needs a separate runtime check with the collector release pinned in the Dockerfile. With Node, Ruby (standard YAML parser) and the official collector binary installed, run from the repository root:

```sh
node tests/collector-regression.mjs /path/to/otelcol-contrib
```

The harness validates the production configuration with a generated key and loopback endpoint, then uses a derived configuration with local file exporters only. Synthetic oversized ASCII/Unicode trace and log attributes must be bounded while numeric values, identity, timing, status and log bodies remain unchanged. Separate file exporters per signal avoid a shared-writer shutdown race observed in 0.160.0. Temporary files and the collector process are cleaned up. Neither this test nor the .NET suite verifies Azure Monitor ingestion; never use real prompts or tenant data as fixtures.

Image builds also run the packaged Linux collector's `validate` command, using a generated instrumentation key and loopback endpoint. This build gate requires no Azure credentials and does not emit telemetry; an invalid configuration must fail the image build. Passing it validates configuration, not live Azure ingestion.

`CopilotSkipCliDownload` skips the bundled CLI download only. These checks do not validate the live Copilot runtime or Azure's tenant-wide quota state. The coordinator and caches are process-local; deployments with multiple backend instances need distributed rate/cooldown coordination.

## API Version References

`AzureApiVersions` keeps separate constants even when operations currently share a version. Request-shape tests check server-generated URLs and tool guidance. The following Microsoft references listed **2026-06-01** when reviewed on 2026-09-08:

- [Query usage](https://learn.microsoft.com/en-us/rest/api/cost-management/query/usage)
- [Forecast usage](https://learn.microsoft.com/en-us/rest/api/cost-management/forecast/usage)
- [Exports list](https://learn.microsoft.com/en-us/rest/api/cost-management/exports/list)
- [Alerts list](https://learn.microsoft.com/en-us/rest/api/cost-management/alerts/list)
- [Scheduled actions list by scope](https://learn.microsoft.com/en-us/rest/api/cost-management/scheduled-actions/list-by-scope)
- [Consumption budgets list](https://learn.microsoft.com/en-us/rest/api/consumption/budgets/list)

Subscription state alone is not evidence of cost or resource absence. See [subscription states](https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/subscription-states); disabled subscriptions still support reads and PastDue remains active.

## Collector Dependency Scan

Remote-image Trivy scans on 2026-09-08 found 5 critical/65 high findings in the 0.108.0 collector Go binary, versus 0 critical/1 high in the official [0.160.0 release](https://github.com/open-telemetry/opentelemetry-collector-releases/releases/tag/v0.160.0). These are dependency matches, not proof that every vulnerable path is reachable. The upgrade does not close all dependency risk:

| Remaining Finding | Dependency | Severity | Scanner Fix |
| --- | --- | --- | --- |
| CVE-2026-79921 | github.com/rabbitmq/amqp091-go v1.12.0 | High | v1.13.0 |
| CVE-2026-56855 | golang.org/x/crypto v0.55.0 | Unknown | v0.56.0 |
| CVE-2026-78662 | golang.org/x/crypto v0.55.0 | Unknown | v0.56.0 |
| GO-2026-5932 | golang.org/x/crypto v0.55.0 | Unknown | Not reported |

RabbitMQ components are not configured by this application, but the finding is not suppressed. Track a patched official distribution and rerun the scan; a clean final application image is a separate gate. Local native config and trace/log tests passed; the Docker daemon was unavailable, so the complete Linux image was not built locally. ACR/container builds must still pass the packaged Linux collector validation before deployment.