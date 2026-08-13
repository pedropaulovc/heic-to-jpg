<#
.SYNOPSIS
    Removes the context menu package, and the self-signed certificate that
    install.ps1 created and trusted.

.DESCRIPTION
    Requires elevation, because the trusted certificate lives in the LocalMachine
    store. Pass -KeepCertificate to remove only the package, which needs no
    elevation: package registration is per user.
#>
[CmdletBinding()]
param(
    [switch]$KeepCertificate
)

$ErrorActionPreference = "Stop"

$publisher   = "CN=Pedro Paulo Vezza Campos"
$packageName = "PedroVezza.HeicToJpeg"

$stateDir       = Join-Path $env:ProgramData "heic-to-jpg"
$thumbprintFile = Join-Path $stateDir "signing-cert.thumbprint"

if (-not $KeepCertificate) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw "This script must run elevated to remove the trusted certificate. Use -KeepCertificate to remove only the package."
    }
}

$package = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($package) {
    Write-Host "Removing $($package.PackageFullName)..." -ForegroundColor Cyan
    Remove-AppxPackage -Package $package.PackageFullName
} else {
    Write-Host "Package $packageName is not installed." -ForegroundColor Yellow
}

# Only ever touch the certificate install.ps1 recorded. The subject is a person's
# name, so deleting every certificate that matches it would take unrelated ones --
# including from LocalMachine\TrustedPeople, where that is a trust decision
# somebody made on purpose.
if (-not $KeepCertificate -and -not (Test-Path $thumbprintFile)) {
    Write-Host "No record of an installer-created certificate; leaving certificates alone." -ForegroundColor Yellow
}

if (-not $KeepCertificate -and (Test-Path $thumbprintFile)) {
    $thumbprint = (Get-Content $thumbprintFile -Raw).Trim()

    Get-ChildItem Cert:\LocalMachine\TrustedPeople, Cert:\CurrentUser\My |
        Where-Object { $_.Thumbprint -eq $thumbprint -and $_.Subject -eq $publisher } |
        ForEach-Object {
            Write-Host "Removing certificate $($_.Thumbprint) from $(Split-Path $_.PSParentPath -Leaf)..." -ForegroundColor Cyan
            Remove-Item $_.PSPath -Force
        }

    Remove-Item $thumbprintFile -Force
}

Write-Host "Restarting Explorer..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }

Write-Host "Uninstalled." -ForegroundColor Green
