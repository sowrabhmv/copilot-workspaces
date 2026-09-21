$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'vendor'
New-Item -ItemType Directory -Path $vendor -Force | Out-Null
$records = @()
foreach ($name in @('core', 'react')) {
    $version = '0.21.0'
    $baseUrl = "https://unpkg.com/@json-render/$name@$version"
    $metadata = Invoke-RestMethod -Uri "$baseUrl/?meta"
    $packageRoot = Join-Path $root ".package-source\$name"
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $files = @()
    foreach ($file in $metadata.files) {
        if ($file.path -notmatch '^/(dist/[A-Za-z0-9_./-]+|package\.json|LICENSE|README\.md)$' -or $file.path.Contains('..')) {
            throw "Unexpected published package path: $($file.path)"
        }
        $relative = $file.path.TrimStart('/').Replace('/', '\')
        $destination = Join-Path $packageRoot $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Invoke-WebRequest -Uri "$baseUrl$($file.path)" -OutFile $destination
        $bytes = [IO.File]::ReadAllBytes($destination)
        $hash = [Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($bytes))
        if ("sha256-$hash" -ne $file.integrity) {
            throw "Integrity mismatch for @json-render/$name $($file.path)"
        }
        $files += @{ path = $file.path; integrity = $file.integrity }
    }
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $packageRoot 'package.json') | ConvertFrom-Json
    if ($manifest.name -ne "@json-render/$name" -or $manifest.version -ne $version) {
        throw "Unexpected package identity for $name"
    }
    npm pack $packageRoot --ignore-scripts --pack-destination $vendor --quiet
    if ($LASTEXITCODE -ne 0) { throw "npm pack failed for $name" }
    $archive = Join-Path $vendor "json-render-$name-$version.tgz"
    $records += @{
        name = $manifest.name
        version = $version
        source = $baseUrl
        license = $manifest.license
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash
        files = $files
    }
}
@{ verifiedAt = '2026-09-20'; reason = 'Configured npm mirror does not yet contain 0.21.0; published artifacts preserved without modification.'; packages = $records } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $vendor 'provenance.json') -Encoding utf8
