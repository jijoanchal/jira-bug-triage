<#
.SYNOPSIS
    Deploys the Failure Triage package to IIS (ASP.NET Core, in-process). Safe to re-run: it updates the site in place.

.DESCRIPTION
    - Creates/updates an app pool (No Managed Code) and a site.
    - Copies the package into -SitePath (using app_offline.htm so files are not locked).
    - Keeps the database and JWT key OUTSIDE the site folder (-DataPath, via the DataDir setting) so redeploys never touch them.
    - Stores settings and secrets as app pool environment variables (applicationHost.config), so redeploys keep them.
    - Raises IIS's upload limit (reports with artifacts can be large) and smoke-tests the site.

    Prerequisites on the server: IIS with the ASP.NET Core Hosting Bundle for .NET 9
    (https://dotnet.microsoft.com/download/dotnet/9.0). Run from an elevated PowerShell.

.PARAMETER Package
    The app folder or zip to deploy. Default: .\app (inside the deployment bundle), else .\publish\app.

.PARAMETER Platform
    x64 or x86: the bitness of the package (and of the app pool). Default: read from platform.txt in the bundle, else x64.

.PARAMETER EnvVars
    Extra environment variables for the app, e.g. @{ ANTHROPIC_API_KEY = 'sk-ant-...'; JIRA_PROJECT_KEY = 'GCR' }.
    Any value left out keeps what was set on a previous deploy. See the README for the full list
    (ANTHROPIC_API_KEY, JIRA_*/CONFLUENCE_*, ADMIN_EMAIL, ADMIN_PASSWORD, JWT_SECRET, Ai__EnforceBudget, ...).

.PARAMETER CopyEnvFromSession
    Also copy the known settings (ANTHROPIC_*, JIRA_*, CONFLUENCE_API_*, ADMIN_*, JWT_SECRET, ARTIFACTS_ROOT) from this
    PowerShell session's environment. Explicit -EnvVars values win.

.EXAMPLE
    .\deploy-iis.ps1 -CopyEnvFromSession
    .\deploy-iis.ps1 -SiteName Triage -HostName triage.company.com -Port 80 -EnvVars @{ ANTHROPIC_API_KEY = 'sk-ant-...' }
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Package,
    [string]$SiteName = 'FailureTriage',
    [int]$Port = 5050,
    [string]$HostName = '',
    [string]$SitePath = 'C:\inetpub\FailureTriage',
    [string]$DataPath = 'C:\inetpub\FailureTriageData',
    [string]$AppPoolName = $SiteName,
    [ValidateSet('x64', 'x86')]
    [string]$Platform,
    [hashtable]$EnvVars = @{},
    [switch]$CopyEnvFromSession
)

$ErrorActionPreference = 'Stop'

function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ---------- prerequisites ----------
Step 'Checking prerequisites'
if (-not (Test-Path "$env:windir\System32\inetsrv\appcmd.exe")) {
    throw 'IIS is not installed. Enable it first, e.g.: Install-WindowsFeature Web-Server, Web-Mgmt-Console (Server) or Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole (Windows 10/11).'
}
Import-Module WebAdministration -ErrorAction Stop
if (-not (Test-Path "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll")) {
    throw 'The ASP.NET Core Module is missing. Install the ASP.NET Core 9 Hosting Bundle (https://dotnet.microsoft.com/download/dotnet/9.0), then run "iisreset" and retry.'
}
if (-not $Platform) {
    $marker = Join-Path $PSScriptRoot 'platform.txt'
    $Platform = if (Test-Path $marker) { (Get-Content $marker -Raw).Trim() } else { 'x64' }
}
if ($Platform -notin 'x64', 'x86') { throw "Unsupported platform '$Platform'; use x64 or x86." }
$dotnetRoot = if ($Platform -eq 'x86' -and ${env:ProgramFiles(x86)}) { "${env:ProgramFiles(x86)}\dotnet" } else { "$env:ProgramFiles\dotnet" }
$runtime = Get-ChildItem "$dotnetRoot\shared\Microsoft.AspNetCore.App" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '9.*' }
if (-not $runtime) { throw "The $Platform ASP.NET Core 9 runtime was not found in $dotnetRoot. Install the ASP.NET Core 9 Hosting Bundle (it includes both x64 and x86)." }
Write-Host "    platform: $Platform"

# ---------- locate the package ----------
$scriptDir = $PSScriptRoot
if (-not $Package) {
    # Inside an unzipped bundle the app sits in .\app; from the repo it is in .\publish\app.
    $Package = @((Join-Path $scriptDir 'app'), (Join-Path $scriptDir 'publish\app')) |
        Where-Object { Test-Path (Join-Path $_ 'Triage.Api.dll') } | Select-Object -First 1
}
if (-not $Package -or -not (Test-Path $Package)) { throw "Package not found. Run this script from the unzipped deployment bundle (next to the app folder), run publish.ps1 first, or pass -Package." }

$temp = $null
if ((Get-Item $Package).PSIsContainer) { $source = (Resolve-Path $Package).Path }
else {
    $temp = Join-Path $env:TEMP ("triage-deploy-" + [guid]::NewGuid().ToString('N'))
    Step "Extracting $Package"
    Expand-Archive -Path $Package -DestinationPath $temp -Force
    $source = $temp
}
if (-not (Test-Path (Join-Path $source 'Triage.Api.dll'))) { throw "Triage.Api.dll not found in $source; this is not a Failure Triage package." }

try {
    # ---------- port / name conflicts ----------
    $clash = Get-Website | Where-Object {
        $_.Name -ne $SiteName -and ($_.Bindings.Collection | Where-Object {
            $_.protocol -like 'http*' -and $_.bindingInformation -eq "*:${Port}:$HostName" })
    }
    if ($clash) { throw "Port $Port$(if ($HostName) { " with host '$HostName'" }) is already used by site '$($clash.Name)'. Choose another -Port or -HostName." }

    # ---------- app pool ----------
    Step "App pool '$AppPoolName' (No Managed Code)"
    if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) { New-WebAppPool -Name $AppPoolName | Out-Null }
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name managedRuntimeVersion -Value ''
    # A 64-bit pool loads the x64 app; enable32BitAppOnWin64 makes the pool load 32-bit (x86) apps instead.
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name enable32BitAppOnWin64 -Value ($Platform -eq 'x86')
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)

    # ---------- copy the app (app_offline.htm releases file locks while we copy) ----------
    Step "Copying files to $SitePath"
    New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
    $offline = Join-Path $SitePath 'app_offline.htm'
    if (Test-Path "IIS:\Sites\$SiteName") {
        Set-Content -Path $offline -Value '<html><body><h3>Updating, back in a moment.</h3></body></html>'
        Start-Sleep -Seconds 3
    }
    & robocopy $source $SitePath /MIR /NFL /NDL /NJH /NJS /NP /XF app_offline.htm | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit code $LASTEXITCODE)." }

    # ---------- data folder and permissions ----------
    Step "Data folder $DataPath (database, JWT key, artifacts)"
    New-Item -ItemType Directory -Force -Path $DataPath | Out-Null
    $identity = "IIS AppPool\$AppPoolName"
    & icacls $DataPath /grant "${identity}:(OI)(CI)M" /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant '$identity' access to $DataPath." }
    & icacls $SitePath /grant "${identity}:(OI)(CI)RX" /T /Q | Out-Null

    # ---------- site ----------
    Step "Site '$SiteName' on port $Port"
    if (-not (Test-Path "IIS:\Sites\$SiteName")) {
        New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $AppPoolName -Port $Port -HostHeader $HostName | Out-Null
    }
    else {
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $SitePath
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
        Clear-ItemProperty "IIS:\Sites\$SiteName" -Name bindings
        New-WebBinding -Name $SiteName -Protocol http -Port $Port -HostHeader $HostName
    }

    # Reports plus artifacts can be large (the app itself accepts up to 1 GB); IIS defaults to 30 MB.
    Set-WebConfigurationProperty -PSPath "IIS:\Sites\$SiteName" -Filter 'system.webServer/security/requestFiltering/requestLimits' `
        -Name maxAllowedContentLength -Value 1073741824

    # ---------- settings and secrets (app pool environment variables) ----------
    Step 'Applying settings'
    $settings = [ordered]@{ ASPNETCORE_ENVIRONMENT = 'Production'; DataDir = $DataPath }
    if ($CopyEnvFromSession) {
        $known = 'ANTHROPIC_API_KEY', 'ANTHROPIC_MODEL', 'JIRA_BASE_URL', 'JIRA_API_USER', 'JIRA_API_TOKEN', 'JIRA_PROJECT_KEY',
                 'JIRA_ISSUE_TYPE', 'JIRA_EMAIL', 'JIRA_API_KEY', 'CONFLUENCE_API_URL', 'CONFLUENCE_API_USER', 'CONFLUENCE_API_TOKEN',
                 'ARTIFACTS_ROOT', 'ADMIN_EMAIL', 'ADMIN_PASSWORD', 'JWT_SECRET'
        foreach ($k in $known) { $v = [Environment]::GetEnvironmentVariable($k); if ($v) { $settings[$k] = $v } }
    }
    foreach ($k in $EnvVars.Keys) { $settings[$k] = [string]$EnvVars[$k] }

    $filter = "system.applicationHost/applicationPools/add[@name='$AppPoolName']/environmentVariables"
    foreach ($k in $settings.Keys) {
        try { Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -AtElement @{ name = $k } -ErrorAction Stop } catch { }
        Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -Value @{ name = $k; value = $settings[$k] }
        Write-Host ("    {0} {1}" -f $k, $(if ($k -match 'KEY|TOKEN|PASSWORD|SECRET') { '= ********' } else { "= $($settings[$k])" }))
    }

    # ---------- go live ----------
    if (Test-Path $offline) { Remove-Item $offline -Force }
    Restart-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    if ((Get-WebAppPoolState $AppPoolName).Value -ne 'Started') { Start-WebAppPool -Name $AppPoolName }
    if ((Get-WebsiteState $SiteName).Value -ne 'Started') { Start-Website -Name $SiteName }

    # ---------- smoke test ----------
    $url = "http://localhost:$Port/"
    if ($HostName) { $headers = @{ Host = $HostName } } else { $headers = @{} }
    Step "Smoke test $url"
    $ok = $false
    foreach ($i in 1..10) {
        try {
            $r = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -TimeoutSec 15
            if ($r.StatusCode -eq 200) { $ok = $true; break }
        }
        catch { Start-Sleep -Seconds 3 }
    }
    if (-not $ok) {
        throw "The site did not respond with 200 at $url. Check the Windows Event Log (Application) and $SitePath\logs, or set stdoutLogEnabled in web.config."
    }
    Write-Host "`nDeployed: $url" -ForegroundColor Green
    Write-Host "Data folder (back this up): $DataPath" -ForegroundColor Green
    if (-not $settings.Contains('ADMIN_PASSWORD')) {
        Write-Host 'First start seeds admin@triage.local with the default password from the README; sign in and change it immediately.' -ForegroundColor Yellow
    }
    if (-not $settings.Contains('ANTHROPIC_API_KEY')) {
        Write-Host 'ANTHROPIC_API_KEY is not set: AI triage is unavailable until you re-run with -EnvVars @{ ANTHROPIC_API_KEY = ... }.' -ForegroundColor Yellow
    }
}
finally {
    if ($temp -and (Test-Path $temp)) { Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue }
}
