---
description: "Audit read-only security enforcement and OAuth permissions for customer deployment"
---

Perform a complete security audit of this agent to verify it is strictly read-only. This is designed for customers cloning this project who want to independently verify the safety guarantees before deploying to their Azure tenant.

### 1. HTTP Method Enforcement

Scan `HttpHelper.ResolveMethod` — the single choke point every pass-through tool uses — and confirm:

- `PUT`, `PATCH` and `DELETE` are rejected (HTTP 403) before any request is sent.
- `POST` is rejected by default and accepted only when the caller passes `allowReadOnlyPost`.
- Only `AzureQueryTools` opts in, and it then validates the path with `ValidateReadOnlyPostPath`.
- List every pattern in that allowlist and verify each is a read-only query/report endpoint.
- Search for any code path that could bypass the choke point (e.g. direct `HttpClient` usage, or a tool calling `HttpHelper.SendCoreAsync`/`SendWithRetryAsync` with a host-built write method).

### 2. Graph and Log Analytics Tools

Scan `GraphQueryTools.cs` and confirm:

- A `method` parameter is exposed to the LLM, but anything other than `GET` is refused by `ResolveMethod` because the tool does not opt into read-only POST.
- The URL is hardcoded to `https://graph.microsoft.com`.

Scan `LogAnalyticsQueryTools.cs` and confirm:

- Only `POST` to `/query` endpoints (`api.loganalytics.io` and `api.applicationinsights.io`).
- KQL is inherently read-only — no write commands exist in the Log Analytics query API.

### 3. OAuth Scopes

Scan `Program.cs` for all OAuth scopes requested during authentication. List every scope and confirm:

- All Microsoft Graph scopes are `.Read` variants (not `.ReadWrite`).
- Log Analytics scope is `Data.Read` (not `Data.ReadWrite`).
- ARM scope is `user_impersonation` — document that this is the only delegated scope ARM offers, and that read-only is enforced at the code level.

### 4. Setup Script

Scan `setup-entra-app.ps1` and confirm:

- All API permissions added are read-only (delegated `Scope` type, not application `Role` type).
- No admin-consent-required write permissions are configured.

### 5. Other Tools

Scan all remaining tool files (`ChartTools.cs`, `HealthTools.cs`, `FaqTools.cs`, `FollowUpTools.cs`, `HtmlPresentationTools.cs`) and confirm:

- No tool makes authenticated HTTP calls to Azure management APIs.
- Any external HTTP calls (e.g. RSS feeds, IndexNow) do not use Azure tokens.

### 6. HttpHelper

Scan `HttpHelper.cs` and confirm:

- `ResolveMethod` is the central method policy, and every pass-through tool routes model-supplied methods through it.
- The transport helpers themselves accept a host-supplied `HttpMethod`, so verify no tool hands them a write method derived from model input.

### 7. Residual write channels

The HTTP guard does not cover the Copilot CLI shell built-ins (`bash`, `powershell`, `rg`), which remain enabled. Confirm:

- `ExcludedBuiltInTools` in `CopilotSessionFactory.cs` and whether the shells are excluded in this deployment.
- The container's own managed-identity role assignments (`infra/modules/roles.bicep`) — image pull and model inference only.
- That the account used to connect holds only Reader / Cost Management Reader if a tenant-side guarantee is required.

### 8. Token Context

Scan `TokenContext.cs` and confirm:

- Tokens are stored per-user with `volatile` fields for thread safety.
- No shared/global tokens that could leak across user sessions.

### Output

Print a summary table:

| Tool              | Methods Allowed | Write Capability | Scope |
| ----------------- | --------------- | ---------------- | ----- |
| QueryAzure        | ...             | ...              | ...   |
| QueryGraph        | ...             | ...              | ...   |
| QueryLogAnalytics | ...             | ...              | ...   |
| (etc.)            | ...             | ...              | ...   |

Then print the full list of OAuth scopes with their access level (read/write).

Flag any findings that deviate from read-only. If everything passes, confirm: **"All tools are verified read-only. Safe for customer deployment."**
