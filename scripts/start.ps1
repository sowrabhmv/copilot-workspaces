[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$CheckProvider
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 10 SDK before starting Workspaces.'
}

if (-not $SkipBuild) {
    if (-not $CheckProvider) {
        if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
            throw 'Install Node.js 22.12 or later before building the browser app.'
        }
        if (-not (Test-Path (Join-Path $root 'web\node_modules'))) {
            & npm ci --prefix web
            if ($LASTEXITCODE -ne 0) { throw 'Browser dependency installation failed.' }
        }
        & npm run build --prefix web
        if ($LASTEXITCODE -ne 0) { throw 'Browser build failed.' }
    }
    & dotnet restore 'src\Workspace.Server\Workspace.Server.csproj' --locked-mode --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Locked .NET restore failed.' }
    & dotnet build 'src\Workspace.Server\Workspace.Server.csproj' --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
}

$applicationArguments = @()
if ($CheckProvider) { $applicationArguments += '--check-provider' }
else { Write-Host 'Workspaces: http://127.0.0.1:5080  (Ctrl+C to stop)' }
& dotnet run --project 'src\Workspace.Server\Workspace.Server.csproj' --no-build --no-restore --no-launch-profile -- @applicationArguments
exit $LASTEXITCODE
