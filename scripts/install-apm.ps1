[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $root '.tools\apm\0.31.0'
$archive = Join-Path $destination 'apm-windows-x86_64.zip'
$checksum = "$archive.sha256"
$baseUrl = 'https://github.com/microsoft/apm/releases/download/v0.31.0'
New-Item -ItemType Directory -Path $destination -Force | Out-Null

Invoke-WebRequest "$baseUrl/apm-windows-x86_64.zip" -OutFile $archive
Invoke-WebRequest "$baseUrl/apm-windows-x86_64.zip.sha256" -OutFile $checksum
$expected = ((Get-Content -Raw -LiteralPath $checksum).Trim() -split '\s+')[0].ToLowerInvariant()
if ($expected -notmatch '^[a-f0-9]{64}$') {
    throw 'The published APM checksum is malformed.'
}
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    throw 'The downloaded APM archive does not match its published SHA-256 checksum.'
}

Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
$executable = Join-Path $destination 'apm-windows-x86_64\apm.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'The verified APM archive does not contain the expected apm.exe.'
}
$previousNoScripts = [Environment]::GetEnvironmentVariable('APM_NO_SCRIPTS', 'Process')
try {
    $env:APM_NO_SCRIPTS = '1'
    $version = (& $executable --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '\bversion\s+0\.31\.0$') {
        throw 'The verified APM executable did not report version 0.31.0.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable('APM_NO_SCRIPTS', $previousNoScripts, 'Process')
}
Remove-Item -LiteralPath $archive, $checksum
Write-Output "Installed scoped APM 0.31.0: $executable"
