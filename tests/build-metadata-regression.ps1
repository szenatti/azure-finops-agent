$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$hook = Join-Path $repoRoot 'infra/scripts/postdeploy.ps1'

function Test-BuildMetadata {
    param([string]$Number, [string]$Branch = 'test/build-metadata', [int]$GitExitCode = 0, [bool]$ExpectFailure = $false)

    $calls = [System.Collections.Generic.List[object]]::new()
    $syntheticSha = [guid]::NewGuid().ToString('N').Substring(0, 7)
    $originalLocation = Get-Location

    function git {
        $global:LASTEXITCODE = $GitExitCode
        if ($args -contains '--short=7') { return $syntheticSha }
        if ($args -contains '--count') { return $Number }
        if ($args -contains '--show-current') { return $Branch }
        throw 'Unexpected Git command in test'
    }
    function azd {
        $global:LASTEXITCODE = 0
        return (@{
            AZURE_CONTAINER_REGISTRY_NAME = '<registry>'
            AZURE_CONTAINER_REGISTRY_IMAGE = '<image>'
            WEB_APP_NAME = '<app>'
            WEB_APP_URL = 'https://example.invalid'
            AZURE_RESOURCE_GROUP = '<resource-group>'
        } | ConvertTo-Json -Compress)
    }
    function az {
        $calls.Add(@($args))
        $global:LASTEXITCODE = 0
    }
    function Test-Path { return $false }
    function Remove-Item { throw 'Test must not remove build outputs' }

    $failure = $null
    try { & $hook *> $null }
    catch { $failure = $_ }

    if ((Get-Location).Path -ne $originalLocation.Path) { throw 'Hook did not restore the working directory' }
    if ($ExpectFailure) {
        if ($null -eq $failure -or $calls.Count -ne 0) { throw 'Invalid metadata must fail before an Azure command' }
    } else {
        if ($failure) { throw $failure }
        if ($calls.Count -ne 2) { throw 'Expected one ACR build and one restart' }
        $buildArgs = $calls[0]
        if ($buildArgs[0] -ne 'acr' -or $buildArgs[1] -ne 'build') { throw 'Expected an ACR build' }
        foreach ($expected in @("BUILD_SHA=$syntheticSha", "BUILD_NUMBER=$Number", "BUILD_BRANCH=$Branch")) {
            $index = [array]::IndexOf($buildArgs, $expected)
            if ($index -lt 1 -or $buildArgs[$index - 1] -ne '--build-arg') {
                throw "Missing build metadata argument: $expected"
            }
        }
    }
    Write-Host "PASS: build number '$Number', branch '$Branch', Git exit $GitExitCode, rejected=$ExpectFailure"
}

Test-BuildMetadata -Number '42'
Test-BuildMetadata -Number '43'
Test-BuildMetadata -Number '0' -ExpectFailure $true
Test-BuildMetadata -Number '' -ExpectFailure $true
Test-BuildMetadata -Number 'invalid' -ExpectFailure $true
Test-BuildMetadata -Number '43' -Branch '' -ExpectFailure $true
Test-BuildMetadata -Number '43' -GitExitCode 128 -ExpectFailure $true
Write-Host 'All build-metadata regressions passed. No Azure commands or filesystem deletions were executed.'