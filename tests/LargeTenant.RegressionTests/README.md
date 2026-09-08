# Large-Tenant Regression Checks

Run from the repository using the SDK pinned in `global.json`:

```sh
dotnet run --project tests/LargeTenant.RegressionTests -p:CopilotSkipCliDownload=true
```

This console harness references the actual Dashboard project without adding test package dependencies and exits nonzero on failure. It does not start the application, use credentials, or call Azure. HTTP responses are supplied by an in-memory handler; the cooldown guard is also tested against a deliberately unreachable host.

Coverage includes retry-header precedence and long waits, tenant isolation and cross-turn cooldown, request pacing, caller-isolated cost caching, a 230-subscription paginated inventory, bounded lookup and result summaries, unsafe continuation rejection, incomplete discovery, and the absence of the budget shortcut for authoritative cost totals.

The Crawl checks exercise the actual tool with 227 synthetic subscriptions, assert a result below 24 KB, verify all seven score/aggregate counts, reject truncated Resource Graph evidence, and check caller-isolated cache reuse. A deliberately blocked HTTP handler verifies deadline cancellation of queued and active work without background requests continuing after the result. The helper tests also verify JSON-visible cooldown metadata and the conservative headerless-429 fallback.

Rate-limit diagnostics tests cover header attribution, unchanged longest-delay selection, bounded values, exclusion of authorization/cookie/unrelated headers, and the absence of invented response headers during local cooldowns.

Collector configuration needs a separate runtime check with the collector release pinned in the Dockerfile. Validate the configuration, then send synthetic oversized OTLP string attributes through a local file exporter. Assert that resource/span/event attributes are bounded while numeric token/status fields and trace identity/timing are unchanged. The .NET suite does not exercise OTTL or Azure Monitor ingestion; never use real prompts or tenant data as collector test fixtures.

Image builds also run the packaged Linux collector's `validate` command, using a generated instrumentation key and loopback endpoint. This build gate requires no Azure credentials and does not emit telemetry; an invalid configuration must fail the image build. Passing it validates configuration, not live Azure ingestion.

`CopilotSkipCliDownload` skips the bundled CLI download only. These checks do not validate the live Copilot runtime or Azure's tenant-wide quota state. The coordinator and caches are process-local; deployments with multiple backend instances need distributed rate/cooldown coordination.