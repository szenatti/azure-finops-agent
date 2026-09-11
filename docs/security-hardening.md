# Security Hardening

The application keeps its existing read-only FinOps tools and limited anonymous demo. It does not enable Azure writes, add permissions, or introduce a second execution service.

## Runtime and Files

The Copilot SDK starts in empty mode, without ambient CLI configuration. Sessions expose exact host-defined tool names and a restricted SDK isolated built-in set. Shell commands, general filesystem tools, nested agents and built-in network requests are not available. Runtime permission requests fail closed except for an exact registered custom tool. Typed file inspection and script/document generation remain supported. Large responses should be narrowed or aggregated at their source; arbitrary shell post-processing is no longer an option.

Artifact producers require a host-bound owner. Downloads, live events and replay check the same owner and 30-minute lifetime. Old ownerless artifacts must be regenerated. A marker from an unrelated tool cannot register or retrieve an artifact.

Public web fetching allows DNS-based HTTPS destinations on port 443. It disables proxy/cookie handling and automatic redirects, validates every redirect, rejects non-public DNS results, and connects directly to the validated address while retaining normal TLS hostname verification. Responses are limited to text/HTML/JSON/XML, five redirects, 600KB and 20 seconds including body reads. HTTP links, IP literals, private endpoints and unsafe redirects are unsupported. These restrictions do not change the dedicated Azure API tools.

## Browser Authentication

`Security__AuthenticationLifetimeHours` defaults to `8`; allowed values are `1` through `168`. The encrypted browser ticket contains an issue time, expiry, tenant/object identity and revocation version. The server validates it on every request. Existing legacy cookies require one fresh sign-in after rollout.

Refreshing Azure access tokens or adding consent does not extend an existing ticket. Expiry removes browser session credentials but preserves conversation files and the encrypted refresh credential used by scheduled jobs. Logout/revoke removes the identity record; disconnect rotates its browser version and retains background credentials. A revoked browser ticket stays invalid after a later login recreates the record. Fresh sign-in requests include `max_age=0` and verify the signed `auth_time` claim.

Every request validates the ticket against the stored record. Because `/home` is a network file share, the decrypted record is cached in memory for 15 seconds, so a revocation or disconnect takes effect within that window rather than instantly. Writes invalidate the entry immediately, and the cache holds text rather than shared objects so a caller that rotates a refresh token cannot poison it. A failed identity write retries briefly and then fails the sign-in with `?azure_error=identity_unavailable` rather than leaving a browser authenticated without a persisted record.

## Workload Defaults

Set individual limits under `Security:Workloads` in configuration, or use environment names such as `Security__Workloads__MaxConcurrentTurns`. All values must be positive.

| Setting | Default |
| --- | ---: |
| MaxConcurrentRequests | 16 |
| MaxConcurrentUploads | 2 |
| MaxConcurrentTurns | 8 |
| MaxConcurrentTurnsPerUser | 2 |
| MaxConcurrentAnonymousTurns | 2 |
| GlobalTurnsPerHour | 200 |
| GlobalAnonymousTurnsPerHour | 120 |
| AuthenticatedTurnsPerHour | 60 |
| AnonymousTurnsPerHour | 5 |
| GlobalRequestsPerHour | 600 |
| GlobalAnonymousRequestsPerHour | 400 |
| AuthenticatedRequestsPerHour | 120 |
| AnonymousRequestsPerHour | 20 |
| MaxPromptCharacters | 16000 |
| AnonymousMaxPromptCharacters | 2000 |
| MaxUploadMegabytes | 100 |
| AnonymousMaxUploadMegabytes | 10 |
| UploadedMegabytesPerUser | 300 |
| AnonymousUploadedMegabytesPerUser | 20 |
| GlobalUploadedMegabytes | 1024 |
| GlobalUploadedFiles | 200 |
| UploadedFilesPerUser | 20 |
| AnonymousUploadedFilesPerUser | 3 |

Anonymous callers are bucketed per browser session, so the per-visitor turn and request budgets above apply to one browser rather than to everyone behind a shared address. Clearing cookies earns a new per-browser bucket, so the `GlobalAnonymous*` ceilings and `MaxConcurrentAnonymousTurns` are what bound total anonymous cost; size them to the demo spend you are willing to fund. Client IP addresses are not used: `X-Forwarded-For` is attacker-controlled here (no trusted proxy policy is configured) and the App Service transport peer is identical for every visitor, so either choice would be spoofable or would collapse the whole demo into one bucket.

Hourly limits use fixed windows starting at first admission. A rejected request does not extend the window. Request quotas cover chat, warmup, reset, session creation, upload and job creation/run requests. Chat and jobs share the same turn budget. A turn slot stays reserved while the backend runs after browser disconnect. Jobs rejected by quotas are rescheduled without a failure strike. Successful admission counts even if later validation or execution fails.

Uploads reserve capacity before copying and remain counted when delisted for an in-flight attachment. Physical deletion or the 30-minute cleanup releases the reservation. Multipart buffering is concurrency-limited. The host's two Python inspection workers have a 30-second deadline including queue time; on Linux each also has a 4 GiB address-space and 25-second CPU limit. The address-space cap counts reserved virtual memory rather than resident memory, so it is deliberately well above a legitimate 100 MB workbook and only stops runaway allocation; validate it against the built image before relying on it.

The UI displays quota/expiry/payload errors and retains unsent prompts. These limits bound requests and concurrency, not an exact currency-denominated Azure spending cap.

## Deployment Boundaries

- These quotas, counters and identity write locks are process-local; counters reset on restart. Run one application instance unless an external/shared admission limiter and identity-write coordination are added. This patch does not provision distributed services.
- Shell restriction is not an operating-system sandbox. Keep managed identity least-privileged, constrain egress and retain container isolation. Typed file readers still process untrusted inputs.
- Validate new/resumed Copilot sessions and deferred tools with the exact bundled Linux CLI before production promotion. Offline policy tests do not prove runtime protocol behavior.
- Verify real Entra sign-in, incremental consent, expiry, logout/revoke across two browser sessions and scheduled-job continuity in an isolated test deployment.
- Scan the exact final image, including Python packages and Copilot runtime. This change does not upgrade dependency findings, harden telemetry content capture or replace existing deployment approval gates.

## Local Verification

```sh
dotnet run --project tests/LargeTenant.RegressionTests -p:CopilotSkipCliDownload=true
npm --prefix src/Dashboard/frontend run build
```

The regression harness includes source-qualified runtime policy, host-tool permissions, artifact owner/expiry checks, public-fetch restrictions, synthetic expiring/revoked identities, shared quotas and actual text-file inspection. It makes no Azure API calls. Run the Linux file-inspection and packaged runtime checks in the final image as well.