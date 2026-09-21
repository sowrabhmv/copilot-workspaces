[CmdletBinding()]
param(
    [string]$ApmExecutable,
    [switch]$UpdateLock
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root 'agent-pack\prepared'
$staging = Join-Path $root 'agent-pack\.build'
$source = Join-Path $root 'agent-pack\source'
$lockPath = Join-Path $root 'apm.lock.yaml'
if ([string]::IsNullOrWhiteSpace($ApmExecutable)) {
    $ApmExecutable = Join-Path $root '.tools\apm\0.31.0\apm-windows-x86_64\apm.exe'
}
$ApmExecutable = [IO.Path]::GetFullPath($ApmExecutable)
if (-not (Test-Path -LiteralPath $ApmExecutable -PathType Leaf)) {
    throw 'Pinned APM 0.31.0 is missing. Run scripts\install-apm.ps1 or pass -ApmExecutable.'
}

$previousHome = [Environment]::GetEnvironmentVariable('APM_HOME', 'Process')
$previousNoScripts = [Environment]::GetEnvironmentVariable('APM_NO_SCRIPTS', 'Process')
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Push-Location $staging
try {
    $env:APM_HOME = Join-Path $root '.tools\apm\home'
    $env:APM_NO_SCRIPTS = '1'
    New-Item -ItemType Directory -Path $env:APM_HOME -Force | Out-Null
    $version = (& $ApmExecutable --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '\bversion\s+0\.31\.0$') {
        throw "Expected APM 0.31.0; received '$version'."
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'apm.yml') -Destination (Join-Path $staging 'apm.yml') -Force
    $sourceParent = Join-Path $staging 'agent-pack'
    New-Item -ItemType Directory -Path $sourceParent -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $sourceParent -Recurse -Force
    $generatedLock = Join-Path $staging 'apm.lock.yaml'
    if (Test-Path -LiteralPath $lockPath) {
        Copy-Item -LiteralPath $lockPath -Destination $generatedLock -Force
    }

    Push-Location (Join-Path $staging 'agent-pack\source')
    try {
        & $ApmExecutable compile --validate --target copilot
        if ($LASTEXITCODE -ne 0) { throw 'APM source validation failed.' }
    }
    finally {
        Pop-Location
    }

    $installArguments = @('install', '--target', 'copilot', '--no-trust-bin')
    if ($UpdateLock) {
        $installArguments += '--update'
    }
    elseif (Test-Path -LiteralPath $lockPath) {
        $installArguments += '--frozen'
    }
    & $ApmExecutable @installArguments
    if ($LASTEXITCODE -ne 0) { throw 'APM installation or frozen lock verification failed.' }
    if ($UpdateLock) {
        # APM 0.31 refreshes local package display versions on its next lock replay.
        & $ApmExecutable install --frozen --target copilot --no-trust-bin
        if ($LASTEXITCODE -ne 0) { throw 'APM updated-lock replay failed.' }
    }

    & $ApmExecutable audit --ci --format json
    if ($LASTEXITCODE -ne 0) { throw 'APM lockfile or deployed-content audit failed.' }

    & $ApmExecutable compile --root $destination --target copilot --force-instructions
    if ($LASTEXITCODE -ne 0) { throw 'APM context compilation failed.' }

    if (-not (Test-Path -LiteralPath $generatedLock -PathType Leaf)) {
        throw 'APM did not generate the expected lockfile.'
    }
    $deployed = Join-Path $staging '.github'
    if (-not (Test-Path -LiteralPath $deployed -PathType Container)) {
        throw 'APM did not deploy the required Copilot prompt files.'
    }
    Copy-Item -LiteralPath $deployed -Destination $destination -Recurse -Force
    $preparedLock = Join-Path $destination 'apm.lock.yaml'
    Copy-Item -LiteralPath $generatedLock -Destination $lockPath -Force
    Copy-Item -LiteralPath $generatedLock -Destination $preparedLock -Force
    Copy-Item -LiteralPath (Join-Path $root 'apm.yml') -Destination (Join-Path $destination 'apm.yml') -Force

    $files = [ordered]@{}
    $paths = [ordered]@{
        common = '.github/instructions/workspace-contract.instructions.md'
        planner = '.github/prompts/planner.prompt.md'
        producer = '.github/prompts/producer.prompt.md'
        reviewer = '.github/prompts/reviewer.prompt.md'
    }
    foreach ($role in $paths.Keys) {
        $path = Join-Path $destination ($paths[$role] -replace '/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "APM did not deploy the required '$role' instruction file."
        }
        $files[$role] = [ordered]@{
            path = $paths[$role]
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $receipt = [ordered]@{
        formatVersion = 1
        name = 'workspace-agent-pack'
        version = '0.2.0'
        apmVersion = '0.31.0'
        lockSha256 = (Get-FileHash -LiteralPath $preparedLock -Algorithm SHA256).Hash.ToLowerInvariant()
        files = $files
    }
    $json = $receipt | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), "$json`n", [Text.UTF8Encoding]::new($false))
    Write-Output "Prepared trusted agent pack: $destination"
}
finally {
    [Environment]::SetEnvironmentVariable('APM_HOME', $previousHome, 'Process')
    [Environment]::SetEnvironmentVariable('APM_NO_SCRIPTS', $previousNoScripts, 'Process')
    Pop-Location
}
