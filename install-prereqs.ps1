<#
.SYNOPSIS
    Installs everything the Failure Triage IIS deployment needs on a Windows server: the IIS role (with the management
    scripting tools deploy-iis.ps1 uses) and the ASP.NET Core 9 Hosting Bundle (.NET 9 runtime + IIS module).
    Safe to re-run: components that are already present are skipped.

.PARAMETER HostingBundleInstaller
    Path to a previously downloaded dotnet-hosting-9.x-win.exe, for servers without internet access.
    Otherwise the latest 9.0 installer is downloaded from Microsoft (https://aka.ms/dotnet/9.0/dotnet-hosting-win.exe).

.PARAMETER OpenFirewallPort
    Also add an inbound Windows Firewall rule for this TCP port (e.g. 5050) so other machines can reach the site.

.EXAMPLE
    .\install-prereqs.ps1
    .\install-prereqs.ps1 -OpenFirewallPort 5050
    .\install-prereqs.ps1 -HostingBundleInstaller D:\installers\dotnet-hosting-9.0.0-win.exe
#>
#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$HostingBundleInstaller,
    [int]$OpenFirewallPort = 0
)

$ErrorActionPreference = 'Stop'
$rebootNeeded = $false

function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }

# ---------- IIS ----------
Step 'IIS role and management tools'
$isServer = (Get-CimInstance Win32_OperatingSystem).ProductType -ne 1
if ($isServer) {
    $features = 'Web-Server', 'Web-WebServer', 'Web-Common-Http', 'Web-Default-Doc', 'Web-Static-Content', 'Web-Http-Errors',
                'Web-Security', 'Web-Filtering', 'Web-Mgmt-Tools', 'Web-Mgmt-Console', 'Web-Scripting-Tools'
    $missing = Get-WindowsFeature -Name $features | Where-Object { -not $_.Installed }
    if ($missing) {
        $r = Install-WindowsFeature -Name $features
        if (-not $r.Success) { throw 'Installing the IIS role failed.' }
        if ($r.RestartNeeded -eq 'Yes') { $rebootNeeded = $true }
        Write-Host "    installed: $($missing.Name -join ', ')"
    }
    else { Write-Host '    already installed' }
}
else {
    $features = 'IIS-WebServerRole', 'IIS-WebServer', 'IIS-CommonHttpFeatures', 'IIS-DefaultDocument', 'IIS-StaticContent',
                'IIS-HttpErrors', 'IIS-Security', 'IIS-RequestFiltering', 'IIS-WebServerManagementTools', 'IIS-ManagementConsole',
                'IIS-ManagementScriptingTools'
    $missing = $features | Where-Object { (Get-WindowsOptionalFeature -Online -FeatureName $_).State -ne 'Enabled' }
    if ($missing) {
        $r = Enable-WindowsOptionalFeature -Online -FeatureName $missing -All -NoRestart
        if ($r.RestartNeeded) { $rebootNeeded = $true }
        Write-Host "    enabled: $($missing -join ', ')"
    }
    else { Write-Host '    already enabled' }
}

# ---------- ASP.NET Core 9 Hosting Bundle (install after IIS so the module registers itself) ----------
Step 'ASP.NET Core 9 Hosting Bundle'
$module = "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
$runtime = Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '9.*' }
if ((Test-Path $module) -and $runtime) {
    Write-Host "    already installed (ASP.NET Core runtime $($runtime[-1].Name))"
}
else {
    $temp = $null
    if (-not $HostingBundleInstaller) {
        $temp = Join-Path $env:TEMP 'dotnet-hosting-win.exe'
        Write-Host '    downloading from https://aka.ms/dotnet/9.0/dotnet-hosting-win.exe'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri 'https://aka.ms/dotnet/9.0/dotnet-hosting-win.exe' -OutFile $temp -UseBasicParsing
        $HostingBundleInstaller = $temp
    }
    elseif (-not (Test-Path $HostingBundleInstaller)) { throw "Installer not found: $HostingBundleInstaller" }

    $sig = Get-AuthenticodeSignature $HostingBundleInstaller
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "The installer is not validly signed by Microsoft (status: $($sig.Status)); refusing to run it."
    }

    Write-Host '    installing (this takes a minute)'
    $p = Start-Process -FilePath $HostingBundleInstaller -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
    if ($p.ExitCode -eq 3010) { $rebootNeeded = $true }
    elseif ($p.ExitCode -ne 0) { throw "The Hosting Bundle installer failed (exit code $($p.ExitCode))." }
    if ($temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }

    # Pick up the new module and PATH so a following deploy works without a new session.
    & iisreset /restart | Out-Null
}

# ---------- firewall ----------
if ($OpenFirewallPort -gt 0) {
    Step "Windows Firewall: allow inbound TCP $OpenFirewallPort"
    $rule = "Failure Triage (TCP $OpenFirewallPort)"
    if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $OpenFirewallPort -Action Allow | Out-Null
    }
    else { Write-Host '    rule already exists' }
}

# ---------- verify ----------
Step 'Verifying'
$ok = (Test-Path "$env:windir\System32\inetsrv\appcmd.exe") -and (Test-Path $module) -and
      [bool](Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.AspNetCore.App" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '9.*' })
if (-not $ok) { throw 'Verification failed: IIS, the ASP.NET Core Module or the .NET 9 runtime is still missing.' }
Write-Host '    IIS, ASP.NET Core Module and .NET 9 runtime are present' -ForegroundColor Green

if ($rebootNeeded) { Write-Host "`nA restart is required before deploying. Restart the server, then run .\deploy-iis.ps1." -ForegroundColor Yellow }
else { Write-Host "`nPrerequisites are ready. Next: .\deploy-iis.ps1" -ForegroundColor Green }
