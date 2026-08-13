<#
.SYNOPSIS
    Builds every artifact into dist\: the converter, the shell extension DLL,
    and the sparse MSIX package.

.PARAMETER SelfContained
    Bundle the .NET runtime into heic2jpg.exe. Produces a much larger output but
    removes the .NET Desktop Runtime prerequisite.
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root    = Split-Path -Parent $PSScriptRoot
$dist    = Join-Path $root "dist"
$objDir  = Join-Path $root "build\obj"
$shellSrc = Join-Path $root "src\ShellExt"

New-Item -ItemType Directory -Force -Path $dist, $objDir | Out-Null

function Find-Tool([string]$name) {
    $found = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
             Sort-Object FullName | Select-Object -Last 1
    if (-not $found) { throw "Could not find $name in the Windows SDK. Install the Windows 10/11 SDK." }
    return $found.FullName
}

# --- 1. Converter -----------------------------------------------------------
Write-Host "[1/3] Publishing heic2jpg.exe..." -ForegroundColor Cyan

$publishArgs = @(
    "publish", (Join-Path $root "src\Heic2Jpg\Heic2Jpg.csproj"),
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-o", $dist,
    "--nologo"
)
if ($SelfContained) {
    $publishArgs += "-p:PublishSingleFile=true"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# --- 2. Shell extension -----------------------------------------------------
Write-Host "[2/3] Compiling HeicToJpegShellExt.dll..." -ForegroundColor Cyan

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "vswhere.exe not found. Install Visual Studio or the Build Tools." }

$vsPath = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                     -latest -format value -property installationPath
if (-not $vsPath) { throw "No Visual Studio installation with the C++ toolset was found." }

$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars." }

# /MT statically links the CRT: a shell extension is loaded into a process we do
# not control, where a missing VC++ redistributable would be an invisible failure.
#
# /Fo needs a trailing separator to mean "directory". A trailing backslash inside
# quotes would escape the closing quote, so use a forward slash -- MSVC accepts
# either, and this keeps paths containing spaces safe.
$res = "$objDir\ShellExt.res"
$clArgs = "/nologo /W4 /WX /EHsc /O2 /MT /DUNICODE /D_UNICODE /LD dllmain.cpp `"$res`"" +
          " /Fo`"$objDir/`" /Fd`"$objDir\ShellExt.pdb`"" +
          " /link /DEF:ShellExt.def /OUT:`"$dist\HeicToJpegShellExt.dll`"" +
          " /IMPLIB:`"$objDir\ShellExt.lib`" ole32.lib shlwapi.lib user32.lib"

$steps = @(
    "call `"$vcvars`" >nul"
    "cd /d `"$shellSrc`""
    "rc /nologo /fo `"$res`" ShellExt.rc"
    "cl $clArgs"
)
$compile = $steps -join " && "

& cmd.exe /c $compile
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }

# --- 3. MSIX ----------------------------------------------------------------
Write-Host "[3/3] Packing HeicToJpeg.msix..." -ForegroundColor Cyan

$makeappx = Find-Tool "makeappx.exe"
$msix = Join-Path $dist "HeicToJpeg.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }

# Only the manifest and logos are packed; the binaries stay in dist\ and are
# reached through -ExternalLocation at install time. /nv skips validation, which
# would otherwise object to heic2jpg.exe not being inside the package.
& $makeappx pack /d (Join-Path $root "package") /p $msix /nv /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE." }

Write-Host ""
Write-Host "Build complete. Artifacts in $dist" -ForegroundColor Green
Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.exe', '.dll', '.msix' } |
    Select-Object Name, @{N = 'Size'; E = { "{0:N0} bytes" -f $_.Length } } | Format-Table -AutoSize
Write-Host "Next: run scripts\install.ps1 from an elevated prompt." -ForegroundColor Yellow
