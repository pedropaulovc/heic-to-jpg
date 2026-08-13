<#
.SYNOPSIS
    Signs the sparse package with a self-signed certificate, trusts that
    certificate machine-wide, and registers the package against dist\.

.DESCRIPTION
    Requires elevation, because trusting a signing certificate writes to the
    LocalMachine certificate store. Everything this creates is removed again by
    uninstall.ps1.
#>
[CmdletBinding()]
param(
    [switch]$SkipExplorerRestart
)

$ErrorActionPreference = "Stop"

# Must match Identity/@Publisher in package\AppxManifest.xml exactly.
$publisher   = "CN=Pedro Paulo Vezza Campos"
$friendly    = "HEIC to JPEG dev signing"
$packageName = "PedroVezza.HeicToJpeg"

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$msix = Join-Path $dist "HeicToJpeg.msix"

# Records which certificate this script created, so uninstall.ps1 removes that one
# and not every certificate that happens to share the subject.
$stateDir       = Join-Path $env:ProgramData "heic-to-jpg"
$thumbprintFile = Join-Path $stateDir "signing-cert.thumbprint"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must run from an elevated PowerShell prompt (it installs a trusted certificate)."
}

# Add-AppxPackage registers per user, for whoever is running it. Elevating into a
# separate administrator account therefore installs the menu for that account and
# not for the person at the keyboard -- a failure that otherwise shows up only as
# "the menu never appeared". Explorer's owner is the account that needs it.
$desktopOwners = @(Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" -ErrorAction SilentlyContinue |
                   ForEach-Object { $o = Invoke-CimMethod -InputObject $_ -MethodName GetOwner
                                    if ($o.ReturnValue -eq 0) { "$($o.Domain)\$($o.User)" } } |
                   Sort-Object -Unique)

if ($desktopOwners.Count -gt 0 -and $desktopOwners -notcontains $identity.Name) {
    throw @"
Running as $($identity.Name), but the desktop belongs to $($desktopOwners -join ', ').

Add-AppxPackage registers the package for the account that runs it, so this would
install the menu for $($identity.Name) and leave the desktop user without it. Elevate
from the desktop user's own session (Start > 'Windows PowerShell' > Run as
administrator, accepting the prompt) rather than supplying another account's
credentials.
"@
}

if (-not (Test-Path $msix)) {
    throw "$msix not found. Run scripts\build.ps1 first."
}
foreach ($required in @("heic2jpg.exe", "HeicToJpegShellExt.dll")) {
    if (-not (Test-Path (Join-Path $dist $required))) {
        throw "$required missing from $dist. Run scripts\build.ps1 first."
    }
}

# --- 1. Signing certificate -------------------------------------------------
# Reuse is keyed on the thumbprint we recorded, never on the subject alone: the
# subject is an ordinary person's name, so an unrelated certificate can carry it
# and must be neither signed with here nor deleted by uninstall.ps1.
$cert = $null
if (Test-Path $thumbprintFile) {
    $known = (Get-Content $thumbprintFile -Raw).Trim()
    $cert  = Get-ChildItem Cert:\CurrentUser\My |
             Where-Object { $_.Thumbprint -eq $known -and
                            $_.Subject -eq $publisher -and
                            $_.NotAfter -gt (Get-Date) -and
                            $_.EnhancedKeyUsageList.ObjectId -contains "1.3.6.1.5.5.7.3.3" } |
             Select-Object -First 1
}

if (-not $cert) {
    Write-Host "[1/4] Creating self-signed code-signing certificate..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $publisher `
        -FriendlyName $friendly `
        -KeyUsage DigitalSignature `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}Subject Type:End Entity")

    New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
    Set-Content -Path $thumbprintFile -Value $cert.Thumbprint -Encoding ascii
} else {
    Write-Host "[1/4] Reusing certificate $($cert.Thumbprint)." -ForegroundColor Cyan
}

# --- 2. Trust it ------------------------------------------------------------
# TrustedPeople is the narrowest store Windows accepts for MSIX signature
# validation. Installing to Trusted Root would trust it for all purposes.
Write-Host "[2/4] Trusting certificate for package validation..." -ForegroundColor Cyan
$cerPath = Join-Path $env:TEMP "heic-to-jpg-signing.cer"
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null
Remove-Item $cerPath -Force -ErrorAction SilentlyContinue

# --- 3. Sign the package ----------------------------------------------------
Write-Host "[3/4] Signing $([IO.Path]::GetFileName($msix))..." -ForegroundColor Cyan
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName | Select-Object -Last 1
if (-not $signtool) { throw "signtool.exe not found. Install the Windows 10/11 SDK." }

& $signtool.FullName sign /fd SHA256 /a /sha1 $cert.Thumbprint $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }

# --- 4. Register ------------------------------------------------------------
Write-Host "[4/4] Registering package with external location $dist..." -ForegroundColor Cyan
Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path $msix -ExternalLocation $dist

if (-not $SkipExplorerRestart) {
    # Explorer caches the context-menu handler list; without a restart the new
    # verb usually does not appear until the next sign-in.
    Write-Host "Restarting Explorer so the new menu entry is picked up..." -ForegroundColor Cyan
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
}

Write-Host ""
Write-Host "Installed. Right-click a .heic file and choose 'Convert to JPEG'." -ForegroundColor Green
