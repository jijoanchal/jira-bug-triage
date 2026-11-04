<#
.SYNOPSIS
    Builds a deployable package: the Angular SPA is built and copied into api\wwwroot, then the API is published
    (Release, framework-dependent) to publish\app and zipped to publish\FailureTriage-<timestamp>.zip.

.PARAMETER SkipWeb
    Skip the web build and reuse the existing api\wwwroot.

.PARAMETER Runtime
    Target platform: win-x64 (default) or win-x86. Only that platform's native libraries are packaged.

.PARAMETER OutDir
    Where to write the package. Default: publish next to this script.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SkipWeb
#>
[CmdletBinding()]
param(
    [switch]$SkipWeb,
    [ValidateSet('win-x64', 'win-x86')]
    [string]$Runtime = 'win-x64',
    [string]$OutDir = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$webDir  = Join-Path $root 'web'
$apiProj = Join-Path $root 'api\Triage.Api.csproj'
$dist    = Join-Path $webDir 'dist\web\browser'
$wwwroot = Join-Path $root 'api\wwwroot'
$appDir  = Join-Path $OutDir 'app'

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
        if (-not (Test-Path (Join-Path $webDir 'node_modules'))) { Invoke-Step 'npm ci' { npm ci } }
        Invoke-Step 'Build web (Angular)' { npm run build }
    }
    finally { Pop-Location }

    if (-not (Test-Path $dist)) { throw "Expected web build output not found: $dist" }
    Write-Host '==> Copy web build to api\wwwroot' -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $wwwroot | Out-Null
    Get-ChildItem -Path $wwwroot -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $dist '*') -Destination $wwwroot -Recurse -Force
}
elseif (-not (Test-Path (Join-Path $wwwroot 'index.html'))) {
    throw 'api\wwwroot has no index.html; run without -SkipWeb.'
}

if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
Invoke-Step "Publish API (Release, $Runtime)" { dotnet publish $apiProj --configuration Release --runtime $Runtime --self-contained false --output $appDir }

# The database and JWT key live in App_Data / DataDir, never in the package.
$leaks = Get-ChildItem -Path $appDir -Recurse -Include '*.db', '*.db-wal', '*.db-shm', 'jwt.key' -ErrorAction SilentlyContinue
if ($leaks) { throw "Package contains data files that must not be deployed: $($leaks.FullName -join ', ')" }

# One self-contained deliverable: app\ + deploy-iis.ps1. Unzip on the IIS server and run the script from there.
$bundleDir = Join-Path $OutDir 'bundle'
if (Test-Path $bundleDir) { Remove-Item -Recurse -Force $bundleDir }
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null
Copy-Item -Path $appDir -Destination (Join-Path $bundleDir 'app') -Recurse
Copy-Item -Path (Join-Path $root 'deploy-iis.ps1'), (Join-Path $root 'install-prereqs.ps1') -Destination $bundleDir
Set-Content -Path (Join-Path $bundleDir 'platform.txt') -Value ($Runtime -replace '^win-', '') -NoNewline   # read by deploy-iis.ps1
Set-Content -Path (Join-Path $bundleDir 'README.txt') -Value @'
Failure Triage - IIS deployment bundle

1. Copy and unzip this bundle on the server, then open an elevated PowerShell in the unzipped folder.
2. Install the prerequisites (IIS + ASP.NET Core 9 Hosting Bundle; skip if already present):
     .\install-prereqs.ps1 -OpenFirewallPort 5050
   (needs internet access, or pass -HostingBundleInstaller <path to dotnet-hosting-9.x-win.exe>; restart if it asks you to)
3. Deploy:
     .\deploy-iis.ps1 -EnvVars @{ ANTHROPIC_API_KEY = '<key>'; ADMIN_PASSWORD = '<strong password>' }
   Site: http://localhost:5050/ (change with -Port / -HostName). Re-run the same command to update in place.
   Run "Get-Help .\deploy-iis.ps1 -Full" for all options (Jira settings, data folder, site name).
'@

$zip = Join-Path $OutDir ("FailureTriage-deploy-{1}-{0:yyyyMMdd-HHmmss}.zip" -f (Get-Date), ($Runtime -replace '^win-', ''))
Write-Host "==> Create $zip" -ForegroundColor Cyan
Get-ChildItem -Path $OutDir -Filter 'FailureTriage-*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force
Compress-Archive -Path (Join-Path $bundleDir '*') -DestinationPath $zip -Force
Remove-Item -Recurse -Force $bundleDir

Write-Host "`nDeployment bundle: $zip" -ForegroundColor Green
Write-Host '(contains app\, install-prereqs.ps1 and deploy-iis.ps1; unzip on the server and run them from an elevated PowerShell)' -ForegroundColor Green
