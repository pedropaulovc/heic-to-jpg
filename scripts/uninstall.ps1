<#
.SYNOPSIS
    Removes the context menu package, and optionally the self-signed certificate
    that install.ps1 trusted.
#>
[CmdletBinding()]
param(
    [switch]$KeepCertificate
)

$ErrorActionPreference = "Stop"

$publisher   = "CN=Pedro Paulo Vezza Campos"
$packageName = "PedroVezza.HeicToJpeg"

$package = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($package) {
    Write-Host "Removing $($package.PackageFullName)..." -ForegroundColor Cyan
    Remove-AppxPackage -Package $package.PackageFullName
} else {
    Write-Host "Package $packageName is not installed." -ForegroundColor Yellow
}

if (-not $KeepCertificate) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($isAdmin) {
        Get-ChildItem Cert:\LocalMachine\TrustedPeople |
            Where-Object { $_.Subject -eq $publisher } |
            ForEach-Object {
                Write-Host "Removing trusted certificate $($_.Thumbprint)..." -ForegroundColor Cyan
                Remove-Item $_.PSPath -Force
            }
    } else {
        Write-Host "Not elevated: leaving the trusted certificate in place." -ForegroundColor Yellow
    }

    Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher } |
        ForEach-Object { Remove-Item $_.PSPath -Force }
}

Write-Host "Restarting Explorer..." -ForegroundColor Cyan
Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }

Write-Host "Uninstalled." -ForegroundColor Green
