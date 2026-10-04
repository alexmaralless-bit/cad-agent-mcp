# reload-nanocad.ps1 — restart NanoCAD so it picks up a freshly built plugin.
#
# Auto-NETLOAD chain (set up by install-autoload.ps1 — run once):
#   system C:\Program Files\Nanosoft\nanoCAD x64 24.1\ncad.lsp
#     └─> %APPDATA%\Nanosoft\nanoCAD x64 24.1\LocalEx\Program\ncad.lsp
#           └─> NETLOAD of artifacts\ClaudeBridge.NanoCad\current\ClaudeBridge.NanoCad.dll
#                 (the 'current' junction is maintained by build.ps1)
# So a fresh build + reload = the new DLL is loaded automatically with no
# manual `(load ...)` or NETLOAD step. If you ever see the bridge fail to
# come up after this script reports success, check that install-autoload.ps1
# has been run (the system ncad.lsp should contain the
# "cad-agent-mcp (nanoCAD) auto-NETLOAD" markers).
#
# Flow:
#   1. (optional) capture the currently open drawing from /summary, so we
#      reopen the same file after restart.
#   2. graceful close (CloseMainWindow -> wait up to -GracefulSeconds).
#   3. force kill if still alive after the grace period.
#   4. start nCad.exe -- NanoCAD's own startup chain runs the autoload hook.
#   5. poll http://127.0.0.1:7890/healthz until 200 OK or timeout.
#
# We do NOT pass any CLI args to nCad.exe -- empirically nCad.exe rejects any
# command-line switches with "Error: 55" (verified with /? in step 7). Passing
# a .dwg path also fails. The drawing is reopened via shell association
# (Start-Process <dwg>) after NanoCAD is up.
#
# Destructive note: graceful close sends WM_CLOSE -- NanoCAD will show its
# usual "Save changes?" dialog if there are unsaved edits. If you don't
# answer within -GracefulSeconds (default 30), the script force-kills and
# unsaved changes are lost. Save manually first if you have work in flight.

[CmdletBinding()]
param(
    # Optional .dwg to reopen via shell association after restart. Set to ""
    # to skip the reopen step (NanoCAD starts with no drawing).
    [string]$Drawing = "",
    # How many seconds to wait for graceful WM_CLOSE before force-killing.
    [int]$GracefulSeconds = 30,
    # How many seconds to wait for the HTTP bridge to come up after restart.
    [int]$HealthzTimeoutSec = 90,
    # Override default NanoCAD path (installation directory varies in some setups).
    [string]$NanoCadExe = "C:\Program Files\Nanosoft\nanoCAD x64 24.1\nCad.exe"
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Test-BridgeAlive {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:7890/healthz" `
            -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop
        return $r.StatusCode -eq 200
    } catch {
        return $false
    }
}

# ---- 1. capture current drawing if not supplied ------------------------
if ([string]::IsNullOrWhiteSpace($Drawing)) {
    if (Test-BridgeAlive) {
        try {
            $s = (Invoke-WebRequest -Uri "http://127.0.0.1:7890/summary" `
                  -TimeoutSec 10 -UseBasicParsing).Content | ConvertFrom-Json
            if ($s.document.filename -and (Test-Path $s.document.filename)) {
                $Drawing = $s.document.filename
                Write-Host "Captured current drawing from bridge: $Drawing"
            }
        } catch {
            Write-Warning "Could not capture current drawing from /summary: $($_.Exception.Message)"
        }
    }
}
if ([string]::IsNullOrWhiteSpace($Drawing)) {
    $fallback = Join-Path $repoRoot 'primer.dwg'
    if (Test-Path $fallback) {
        $Drawing = $fallback
        Write-Host "Falling back to drawing: $Drawing"
    } else {
        $Drawing = ""
        Write-Host "No drawing to reopen (none captured, no fallback found)."
    }
}

# ---- 2-3. graceful close → force fallback ------------------------------
$procs = @(Get-Process -Name nCad -ErrorAction SilentlyContinue)
if ($procs.Count -gt 0) {
    Write-Host "Asking $($procs.Count) NanoCAD process(es) to close gracefully..."
    foreach ($p in $procs) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }

    $deadline = (Get-Date).AddSeconds($GracefulSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process -Name nCad -ErrorAction SilentlyContinue)) { break }
    }

    $remaining = @(Get-Process -Name nCad -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        Write-Warning "Graceful timeout ($GracefulSeconds s) — force-killing $($remaining.Count) process(es). Unsaved work may be lost."
        $remaining | Stop-Process -Force
        Start-Sleep -Seconds 2
    } else {
        Write-Host "NanoCAD closed gracefully."
    }
} else {
    Write-Host "No running NanoCAD process found."
}

# ---- 4. start NanoCAD (no CLI args — they all error with "55") ---------
if (-not (Test-Path $NanoCadExe)) {
    throw "NanoCAD executable not found at: $NanoCadExe. Pass -NanoCadExe to override."
}

$dllPath = Join-Path $repoRoot 'artifacts\ClaudeBridge.NanoCad\current\ClaudeBridge.NanoCad.dll'
if (-not (Test-Path $dllPath)) {
    Write-Warning "Stable DLL path missing: $dllPath. Run build.ps1 first (it creates the 'current' junction)."
}

# Verify the user-level ncad.lsp exists, otherwise auto-NETLOAD won't fire and
# the bridge will never come up — give the user a clear hint instead of timing
# out at step 5 with no explanation.
# Resolve against the REAL user APPDATA (not whatever $env:APPDATA the parent
# shell has — build.ps1 isolates that for its dotnet build sandbox, and if
# you ran reload from the same shell it would point inside the repo).
$realAppData = [Environment]::GetFolderPath('ApplicationData')
$userLsp = Join-Path $realAppData 'Nanosoft\nanoCAD x64 24.1\LocalEx\Program\ncad.lsp'
if (-not (Test-Path $userLsp)) {
    Write-Warning "User ncad.lsp not found at: $userLsp. Without it the plugin will not auto-load on startup and the healthz poll below will time out."
}

Write-Host "Starting NanoCAD..."
# WorkingDirectory left at the parent shell's default. The auto-NETLOAD chain
# does not depend on it -- it goes through the system ncad.lsp hook installed
# by install-autoload.ps1. (Earlier experiment with acaddoc.lsp in the repo
# root showed that NanoCAD finds the file via findfile but does NOT auto-
# execute AutoCAD-convention names on startup -- the only working path is
# the system ncad.lsp hook.)
Start-Process -FilePath $NanoCadExe | Out-Null

# NOTE: do NOT use `Start-Process <drawing.dwg>` to reopen the file.
# Windows shell association spawns a SECOND nCad.exe process with whichever
# profile is registered as the .dwg default (e.g. SPDS), instead of opening
# the file inside the NanoCAD we just started. The result was two running
# NanoCADs — first one with the plugin loaded but empty, second one with
# the drawing but no plugin (port 7890 already taken by the first).
#
# Instead: wait for the bridge to come up, then send `_OPEN` via the bridge
# itself. That hits SendStringToExecute in the already-running NanoCAD,
# which opens the drawing in-process.

# ---- 5. poll healthz ---------------------------------------------------
Write-Host "Waiting up to $HealthzTimeoutSec s for HTTP bridge..."
$deadline = (Get-Date).AddSeconds($HealthzTimeoutSec)
$ok = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if (Test-BridgeAlive) { $ok = $true; break }
}

if ($ok) {
    $elapsed = [int]($HealthzTimeoutSec - ($deadline - (Get-Date)).TotalSeconds)
    Write-Host "Bridge is up. Total wait: ~$elapsed s."
} else {
    Write-Error "Timeout waiting for HTTP bridge after $HealthzTimeoutSec s. NanoCAD may have started without loading the plugin — check the command line in the NanoCAD UI for NETLOAD output."
    exit 1
}

# ---- 6. drawing reopen: handled by the plugin (Q23) --------------------
# As of Q23, the plugin subscribes to DocumentManager.DocumentCreated in
# Initialize() and auto-opens the configured default drawing via the
# managed DocumentManager.Open(path, false) API. No action needed here.
#
# The default drawing comes from env var CLAUDE_BRIDGE_DEFAULT_DRAWING
# (per-machine override); without it auto-open is skipped.
#
# History (kept as a "don't do that" note):
#  - Start-Process <drawing.dwg> would spawn a SECOND nCad.exe under the
#    shell-association profile (e.g. SPDS), leaving the plugin in one
#    process and the drawing in another with port 7890 collision.
#  - (command "._OPEN" "<path>") via /exec is blocked by ExecCommand's
#    backup requirement when the fresh NanoCAD is sitting on unsaved
#    Drawing1.
# The DocumentCreated managed-event path is the right answer.

exit 0
