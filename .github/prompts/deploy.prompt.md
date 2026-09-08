---
agent: agent
description: "Validate releases and provide staged Azure VM PowerShell deployment steps, or deploy using the explicitly requested Azure workflow"
---

## Deploy Azure FinOps Agent

Deploy only after the user explicitly confirms the target environment and asks to deploy. A request for deployment steps is a handoff, not permission to run Azure deployment commands. Never infer targets or put tenant IDs, subscription IDs, resource groups, app names, registry names, endpoints, IP addresses, or credentials into tracked files. Previously supplied, unambiguous non-secret target values may be reused in chat-only PowerShell commands; do not ask for them again.

### 1. Establish the target

1. Inspect `git status --short`; do not deploy uncommitted or unreviewed changes.
2. Read `azure.yaml`, the current `azd` environment, and GitHub Actions configuration.
3. Show the active Azure account with `az account show` and ask for confirmation if the intended tenant/subscription is not already unambiguous.
4. Resolve deployment coordinates from one of these external sources:
   - Customer deployment: `azd env get-values`
   - Maintainer CI: GitHub Actions repository variables and secrets
   - Manual recovery: user-supplied values for this run only
5. Never write resolved values into tracked files.

### 2. Validate

```powershell
cd src/Dashboard/frontend
npm ci
npm run build

cd ..
dotnet build Dashboard.csproj --no-restore
```

Run relevant tests and inspect the complete diff. Scan tracked changes for secrets, GUIDs, resource IDs, host-specific names, email addresses, and IP addresses before proceeding.

### 3. Deploy

For a new customer environment, prefer the customer-supported Azure Developer CLI path:

```powershell
azd up
```

For maintainer CI, push an already-reviewed commit to the branch mapped by the workflow. The workflow must read all target coordinates from GitHub Actions configuration and authenticate with OIDC.

For an explicitly requested update/recovery through an Azure Windows VM, follow the two-stage handoff below instead of substituting an `azd up` flow. Resolve the registry, image, web app, resource group, and verification URL from prior user-supplied context or external configuration; never store their real values in this prompt.

### Azure VM PowerShell Handoff

Use this format whenever the user asks for deployment steps through their Azure VM. Do not reproduce old commit hashes, image tags, timing claims, or warnings from an example as current facts.

#### Release Preparation

- Keep the change scoped to the requested release; leave unrelated work out. Maintain an incremental task list for multi-step release work and give brief progress updates.
- Inspect worktree status, diff, actual remotes, branch and workflow triggers. Preserve existing edits; do not assume `origin` points to the upstream sample repository.
- Run relevant regression/build checks and scan all staged additions, including new files, for sensitive literals before committing. Commit or push only when authorized; warn about push-triggered deployments. Verify the remote branch SHA after pushing, then report the published commit and worktree status.
- Distinguish native collector checks from exact Linux image validation. If local Docker is unavailable, disclose it; the ACR build must still validate the packaged Linux collector/configuration using generated test-only settings. Never bypass that gate or claim it passed before seeing the build result.
- Report unresolved security findings and unverified requirements separately. A successful build is not security clearance or proof that the Web App is serving the release.

#### Stage 1: Update and Build Only

Begin with a concise release summary: what was published, the exact commit, checks performed, and remaining cautions. Then provide these numbered sections:

1. **Update the VM.** Reuse the user's existing repository path by default. Show `git status --short` first and stop if local changes need preserving. Provide fetch, switch to the intended branch, `pull --ff-only`, and commit verification as separate commands/short blocks. Confirm the expected published SHA before building. Do not introduce a fresh temporary clone unless requested or a concrete checkout problem requires it.
2. **Prepare the Build.** Reuse known target names and subscription values in chat-only assignments. Ask only for genuinely missing or ambiguous values. Use stable variable names across both stages: `$SUB`, `$RG`, `$APP`, `$ACR`, `$REGISTRY`, `$SHA`, `$BRANCH`, `$NUMBER`, `$TAG`, `$SITE_URL`, `$ROLLBACK_IMAGE`. Derive SHA, branch and build number from the verified checkout; generate a timestamp-plus-SHA image tag once. Resolve the actual registry login server and App Service hostname rather than assuming their DNS format. Confirm Azure account/target context and identify production versus a named slot explicitly.
3. **Build in ACR.** Ensure the build context contains no local secrets or generated output. Preserve the existing checkout; if ignored outputs make it unsuitable, use a clean tracked-file export for the build context. Show a readable, multiline `az acr build` command with PowerShell backticks, explicit subscription, registry, tag, `linux/amd64`, `src/Dashboard/Dockerfile`, `BUILD_SHA`, `BUILD_NUMBER`, `BUILD_BRANCH`, and the `src/Dashboard` context (or its clean export equivalent).

For VM builds, recalculate `$NUMBER = (git rev-list --count HEAD).Trim()` after updating and verifying the release checkout; check the exit code and require a positive integer. Never reuse a previous PowerShell window's build number or copy one from an earlier response. Pass it explicitly as `--build-arg "BUILD_NUMBER=$NUMBER"` alongside the refreshed SHA and branch. Use the full Git history for a meaningful commit count. The `azd` postdeploy hook uses the same convention; GitHub Actions uses its workflow run number. A rebuild of the same commit keeps the commit-count build number, while the timestamp-plus-SHA image tag distinguishes the image build.

End with: **Stop here and check the ACR build result before updating the Web App.** Ask for the result. Do not include or execute Web App promotion commands in this first handoff unless the user explicitly requests the full procedure at once.

#### Stage 2: Promote Only After Build Confirmation

Read the user's ACR output first. Confirm the build and image push succeeded, identify the exact tag, and check the packaged Linux collector validation result. Report timings or step numbers only if present in the output. If build status is missing or failed, resolve that before promotion. Repeat unresolved image-security cautions; do not silently treat them as resolved.

Tell the user to run the following sections separately in the same VM PowerShell window, stopping on any error:

1. **Set Deployment Variables.** Repeat the known non-secret target values and exact successfully built tag so the block is usable after a context interruption. Do not generate a new tag or rebuild. Carry forward the expected SHA, branch and build number for verification.
2. **Capture Rollback Image.** Read `linuxFxVersion` from the intended Web App/slot. Require a successful command, a nonempty value starting with `DOCKER|`, and a usable image reference. Store the prefix-stripped value in `$ROLLBACK_IMAGE`, display old/new image references, and stop if validation fails. Do not overwrite the rollback value during retries. If the app uses another container configuration model, stop and adapt the procedure rather than guessing.
3. **Update the Web App.** Point the same target at the confirmed ACR image with `az webapp config container set`. Preserve managed-identity image pull and existing settings; do not enable registry credentials or reconfigure infrastructure.
4. **Restart Once.** Explain that changing container configuration already triggers a restart. If including an explicit restart to match the operational handoff, issue it at most once after a successful update, then allow startup. Do not repeatedly restart or rebuild when the old version still appears. Any bounded startup wait in user-facing commands is not evidence of readiness.
5. **Verify.** Show `/api/version` and the configured `linuxFxVersion`; compare the expected SHA, branch, build number and image tag. Ask the user to share those two non-secret outputs. If mismatched, inspect image startup/pull failures and build-metadata overrides before recommending another build or restart. Retain the rollback image for recovery; rollback itself requires approval.

Require `/api/version.build` to equal the `$NUMBER` passed to the successful build, not merely a nonzero value. Refresh the browser after serving metadata matches so the top-right label reloads. Do not change App Service `BUILD_*` settings just to make the label look current; runtime overrides can misidentify the image and must be diagnosed against its build metadata.

#### PowerShell Presentation and Safety

- Use short, numbered sections and separate runnable blocks, not one large automation script. Keep variable names consistent between replies. Run each native command separately or check `$LASTEXITCODE` immediately before later commands can overwrite it.
- Do not rely on `$ErrorActionPreference = 'Stop'` alone for native-command failures. Group dependent safety checks in a short script block so `throw` prevents its remaining actions. Variables needed by subsequent blocks must remain available in the same PowerShell session.
- Keep all real deployment coordinates in chat/terminal only. Tracked templates and instructions use placeholders or describe runtime resolution; never copy customer logs, identifiers or personal paths into them.
- Providing commands does not mean they ran. Distinguish committed, pushed, ACR-built, configured and verified-serving states, and state clearly when no Azure deployment was performed by the assistant.

### 4. Verify

1. Wait for deployment completion and require a successful deployment status.
2. Read the verification URL from deployment output or external configuration.
3. Check `/api/version` until it reports the expected commit/build.
4. Run a production smoke test without exposing credentials or customer data.
5. Report the deployed commit, target type, verification result, and any rollback action taken.

### Rules

- Never deploy automatically or without explicit user approval.
- Never commit deployment coordinates or credentials.
- Prefer managed identity and OIDC over client secrets.
- Do not weaken the application's no-delete security boundary.
- Do not report success from a build alone; verify the running endpoint.
