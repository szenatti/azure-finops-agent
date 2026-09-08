# Large-Tenant Regression Checks

Run from the repository using the SDK pinned in `global.json`:

```sh
dotnet run --project tests/LargeTenant.RegressionTests -p:CopilotSkipCliDownload=true
```

This console harness references the actual Dashboard project without adding test package dependencies and exits nonzero on failure. It does not start the application, use credentials, or call Azure. HTTP responses are supplied by an in-memory handler; the cooldown guard is also tested against a deliberately unreachable host.

Coverage includes retry-header precedence and long waits, tenant isolation and cross-turn cooldown, request pacing, caller-isolated cost caching, a 230-subscription paginated inventory, bounded lookup and result summaries, unsafe continuation rejection, incomplete discovery, and the absence of the budget shortcut for authoritative cost totals.

The Crawl checks exercise the actual tool with 227 synthetic subscriptions, assert a result below 24 KB, verify all seven score/aggregate counts, reject truncated Resource Graph evidence, and check caller-isolated cache reuse. A deliberately blocked HTTP handler verifies deadline cancellation of queued and active work without background requests continuing after the result. The helper tests also verify JSON-visible cooldown metadata and the conservative headerless-429 fallback.

`CopilotSkipCliDownload` skips the bundled CLI download only. These checks do not validate the live Copilot runtime or Azure's tenant-wide quota state. The coordinator and caches are process-local; deployments with multiple backend instances need distributed rate/cooldown coordination.