<#
.SYNOPSIS
    Repro for the collision-safe naming rule: N converters racing for one source
    must produce N distinct, valid JPEGs and clobber nothing.

.DESCRIPTION
    Explorer starts a converter process per batch, so two batches over the same
    folder -- or an impatient second right-click -- run concurrently. Checking
    File.Exists and then writing would let the second process replace the first
    one's output; the converter instead claims a name with a non-overwriting move.

    Exits non-zero if any name is reused, any output fails to decode, or any
    temporary file is left behind.
#>
[CmdletBinding()]
param(
    [string]$SourceHeic,
    [int]$Racers = 4
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$exe  = Join-Path $root "dist\heic2jpg.exe"
if (-not (Test-Path $exe)) { throw "$exe not found. Run scripts\build.ps1 first." }

if (-not $SourceHeic) {
    $SourceHeic = Get-ChildItem "$env:USERPROFILE\Pictures" -Recurse -Include *.heic, *.heif -ErrorAction SilentlyContinue |
                  Select-Object -First 1 -ExpandProperty FullName
}
if (-not $SourceHeic -or -not (Test-Path $SourceHeic)) {
    throw "No source image. Pass -SourceHeic <path to a .heic>."
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("heic2jpg-race-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null
$source = Join-Path $work "race.heic"
Copy-Item $SourceHeic $source

try {
    Write-Host "Racing $Racers converters over $(Split-Path $source -Leaf)..." -ForegroundColor Cyan
    $procs = 1..$Racers | ForEach-Object { Start-Process -FilePath $exe -ArgumentList "`"$source`"" -PassThru -WindowStyle Hidden }
    $procs | Wait-Process -Timeout 120

    $failed = $procs | Where-Object ExitCode -ne 0
    if ($failed) { throw "$($failed.Count) converter process(es) exited non-zero." }

    $jpgs = @(Get-ChildItem $work -Filter *.jpg)
    $partials = @(Get-ChildItem $work -Filter *.partial)

    Write-Host ("produced: " + (($jpgs | ForEach-Object Name) -join ", "))

    if ($jpgs.Count -ne $Racers) { throw "expected $Racers distinct JPEGs, got $($jpgs.Count) -- a name was reused." }
    if ($partials.Count -ne 0)   { throw "$($partials.Count) temporary file(s) left behind." }

    # A clobbered output is usually a truncated or unreadable file, so decode each.
    foreach ($jpg in $jpgs) {
        $img = [System.Drawing.Image]::FromFile($jpg.FullName)
        try {
            if ($img.Width -le 0 -or $img.Height -le 0) { throw "$($jpg.Name) has no pixels." }
        } finally { $img.Dispose() }
    }

    Write-Host "PASS: $($jpgs.Count) distinct JPEGs, all decodable, nothing overwritten." -ForegroundColor Green
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
