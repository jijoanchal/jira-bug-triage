<#
.SYNOPSIS
    Builds the Angular SPA, copies it into the API's wwwroot, builds the API and starts it (single host).

.PARAMETER Url
    URL the API listens on. Default http://localhost:5145.

.PARAMETER SkipWeb
    Skip the web build and reuse the existing api/wwwroot.

.PARAMETER BuildOnly
    Build only; do not start the API.

.PARAMETER Configuration
    dotnet build configuration. Default Debug.

.EXAMPLE
    .\build-and-start.ps1
    .\build-and-start.ps1 -SkipWeb
    .\build-and-start.ps1 -BuildOnly
#>
[CmdletBinding()]
param(
    [string]$Url = 'http://localhost:5145',
    [switch]$SkipWeb,
    [switch]$BuildOnly,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$webDir  = Join-Path $root 'web'
$apiDir  = Join-Path $root 'api'
$dist    = Join-Path $webDir 'dist\web\browser'
$wwwroot = Join-Path $apiDir 'wwwroot'

function Invoke-Step([string]$Name, [scriptblock]$Action) {
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)." }
}

foreach ($tool in @('dotnet') + $(if (-not $SkipWeb) { 'npm' })) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "'$tool' was not found on PATH." }
}

if (-not $SkipWeb) {
    Push-Location $webDir
    try {
        if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
            Invoke-Step 'npm ci' { npm ci }
        }
        Invoke-Step 'Build web (Angular)' { npm run build }
    }
    finally { Pop-Location }

    if (-not (Test-Path $dist)) { throw "Expected web build output not found: $dist" }

    Write-Host "==> Copy web build to api\wwwroot" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
    Get-ChildItem -Path $wwwroot -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force
}

Push-Location $apiDir
try {
    Invoke-Step 'Build API (.NET)' { dotnet build --configuration $Configuration }

    if ($BuildOnly) {
        Write-Host 'Build complete.' -ForegroundColor Green
        return
    }

    Write-Host "==> Starting API at $Url (Ctrl+C to stop)" -ForegroundColor Green
    dotnet run --no-build --configuration $Configuration --urls $Url
}
finally { Pop-Location }
