[CmdletBinding()]
param(
    # Number of most-recent timestamped build directories to retain after a successful
    # build. Older ones are removed to keep artifacts/ small. Set higher if you want
    # more rollback room; set to 0 to disable cleanup. Locked directories (e.g. a DLL
    # currently held by AutoCAD) are skipped silently — they'll be removed on the next
    # build after NETLOAD picks up a newer DLL.
    [int]$KeepLast = 3
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet_home'
$env:LOCALAPPDATA = Join-Path $repoRoot '.appdata\local'
$env:APPDATA = Join-Path $repoRoot '.appdata\roaming'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME | Out-Null
New-Item -ItemType Directory -Force $env:LOCALAPPDATA | Out-Null
New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
New-Item -ItemType Directory -Force $env:NUGET_PACKAGES | Out-Null

$artifactsRoot = Join-Path $repoRoot 'artifacts\ClaudeBridge.AutoCad'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$outputRoot = Join-Path $artifactsRoot $stamp
New-Item -ItemType Directory -Force $outputRoot | Out-Null

& dotnet build (Join-Path $repoRoot 'src\AutoCad\ClaudeBridge.AutoCad.csproj') --configuration Debug -p:OutputPath="$outputRoot\" -p:AppendTargetFrameworkToOutputPath=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$latestFile = Join-Path $artifactsRoot 'latest.txt'
Set-Content -Path $latestFile -Value $outputRoot -Encoding UTF8
Write-Host "Plugin output: $outputRoot"
Write-Host "NETLOAD: $(Join-Path $outputRoot 'ClaudeBridge.AutoCad.dll')"

# Stable path for auto-load: junction artifacts\...\current → fresh timestamped dir.
# Junction works without admin (unlike symlink). The reload script and the
# nanocad_autoload.scr file both reference this stable path, so they don't
# need to know the timestamp of the latest build. AutoCAD's NETLOAD reads the
# DLL through the junction transparently.
$currentJct = Join-Path $artifactsRoot 'current'
if (Test-Path $currentJct) {
    # Remove the junction itself (not its target). Remove-Item -Force on a
    # junction unlinks it; the timestamped folder underneath is untouched.
    try { Remove-Item -Force -Recurse $currentJct } catch { Write-Warning "Could not remove existing 'current' junction: $($_.Exception.Message)" }
}
try {
    New-Item -ItemType Junction -Path $currentJct -Target $outputRoot -ErrorAction Stop | Out-Null
    Write-Host "Stable path:   $currentJct -> $outputRoot"
} catch {
    Write-Warning "Could not create 'current' junction: $($_.Exception.Message). nanocad_autoload.scr will still reference 'current' but will fail until this is resolved."
}

# ---- post-build cleanup -------------------------------------------------
if ($KeepLast -gt 0) {
    # Match only timestamped directories (8-digit date + underscore + 6-digit time).
    # This filter protects latest.txt and anything else a human might drop in.
    $stampedDirs = Get-ChildItem -Path $artifactsRoot -Directory `
        | Where-Object { $_.Name -match '^\d{8}_\d{6}$' } `
        | Sort-Object Name -Descending

    if ($stampedDirs.Count -gt $KeepLast) {
        $toRemove = $stampedDirs | Select-Object -Skip $KeepLast
        $removed = 0
        $skipped = 0
        foreach ($dir in $toRemove) {
            try {
                Remove-Item -Recurse -Force -ErrorAction Stop $dir.FullName
                $removed++
            } catch {
                # Most likely a AutoCAD process still holds a DLL inside this folder.
                # Leave it; the next build will try again after NETLOAD reloads a newer DLL.
                $skipped++
            }
        }
        $kept = [Math]::Min($stampedDirs.Count, $KeepLast)
        Write-Host "Cleanup: removed $removed old build dir(s), kept $kept most recent, skipped $skipped (locked)."
    }
}
