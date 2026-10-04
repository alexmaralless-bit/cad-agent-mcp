# install-autoload.ps1 -- one-time install of cad-agent-mcp (nanoCAD) auto-NETLOAD hook into
# the NanoCAD startup chain. After this runs once, NanoCAD will load the
# ClaudeBridge plugin automatically on every start -- no more manual
# (load "...") in the command line.
#
# Mechanism: appends a single (load "<user-ncad.lsp>") block to the system
# nCad.lsp in `C:\Program Files\Nanosoft\nanoCAD x64 24.1\`. That system file
# is read by NanoCAD on startup; our block then loads our user-level
# ncad.lsp from `%APPDATA%\Nanosoft\nanoCAD x64 24.1\LocalEx\Program\`, which
# in turn issues NETLOAD on the plugin DLL via the build.ps1-managed
# `current\` junction.
#
# Why this gymnastics?
# - %APPDATA%\...\LocalEx\Program\ncad.lsp is NOT in NanoCAD's default
#   findfile search path (verified empirically -- only the Program Files
#   ncad.lsp is found).
# - There's no registry knob for additional support paths (verified by
#   walking HKCU\Software\Nanosoft\nanoCAD x64\24.1\Profiles).
# - Editing Program Files needs admin elevation. We do that once here so
#   the rest of the workflow needs no elevation.
#
# Idempotent: a sentinel comment marks our block. Re-running does nothing
# if already installed. To uninstall, delete the marked block manually
# (or restore the .bak file we keep alongside).

[CmdletBinding()]
param(
    [string]$NanoCadSystemLsp = "C:\Program Files\Nanosoft\nanoCAD x64 24.1\ncad.lsp",
    [string]$UserNcadLsp = (Join-Path $env:APPDATA 'Nanosoft\nanoCAD x64 24.1\LocalEx\Program\ncad.lsp')
)

$ErrorActionPreference = 'Stop'

# Sentinel used to detect a prior install and to bracket our block for
# easy manual removal.
$markerOpen  = ';; cad-agent-mcp (nanoCAD) auto-NETLOAD: BEGIN (managed by install-autoload.ps1)'
$markerClose = ';; cad-agent-mcp (nanoCAD) auto-NETLOAD: END'

# ---- elevation check ---------------------------------------------------
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal $me
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevation required to edit '$NanoCadSystemLsp'. Re-launching with UAC prompt..."
    $scriptPath = $MyInvocation.MyCommand.Path
    $passArgs = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$scriptPath`"",
        '-NanoCadSystemLsp', "`"$NanoCadSystemLsp`"",
        '-UserNcadLsp', "`"$UserNcadLsp`""
    )
    try {
        Start-Process powershell -Verb RunAs -ArgumentList $passArgs -Wait
        Write-Host "Elevated install attempt finished (see the elevated console window for details)."
        exit 0
    } catch {
        Write-Error "User declined the UAC prompt, or elevation failed: $($_.Exception.Message)"
        exit 1
    }
}

Write-Host "Running elevated. Installing auto-NETLOAD hook."

# ---- sanity checks -----------------------------------------------------
if (-not (Test-Path $NanoCadSystemLsp)) {
    throw "System nCad.lsp not found at: $NanoCadSystemLsp"
}
if (-not (Test-Path $UserNcadLsp)) {
    throw "User ncad.lsp not found at: $UserNcadLsp -- this script expects the user-level ncad.lsp from the cad-agent-mcp (nanoCAD) project to already exist."
}

# ---- idempotency -------------------------------------------------------
$content = Get-Content -Raw -Encoding UTF8 -LiteralPath $NanoCadSystemLsp
if ($content -match [regex]::Escape($markerOpen)) {
    Write-Host "Auto-NETLOAD hook is already installed in $NanoCadSystemLsp. Nothing to do."
    Write-Host "(To force a fresh install, delete the marked block between '$markerOpen' and '$markerClose', then re-run.)"
    exit 0
}

# ---- backup ------------------------------------------------------------
$bak = "$NanoCadSystemLsp.bak"
if (-not (Test-Path $bak)) {
    Copy-Item -LiteralPath $NanoCadSystemLsp -Destination $bak
    Write-Host "Backup saved: $bak"
} else {
    Write-Host "Backup already exists: $bak (left alone)"
}

# ---- compose block -----------------------------------------------------
# LISP needs double-backslash escapes for Windows paths.
$lispPath = $UserNcadLsp -replace '\\', '\\'
$block = @"

$markerOpen
;; This block was added by install-autoload.ps1 from the cad-agent-mcp (nanoCAD) project.
;; It loads the user-level ncad.lsp, which in turn NETLOADs the
;; ClaudeBridge.NanoCad plugin via the build.ps1 'current' junction.
;; To disable: delete everything from the BEGIN line to the END line below
;; (or restore $bak).
(if (findfile "$lispPath")
  (load "$lispPath")
  (princ "\ncad-agent-mcp (nanoCAD): user ncad.lsp not found at $lispPath")
)
(princ)
$markerClose
"@

# ---- write -------------------------------------------------------------
# Append the block, preserving original encoding by re-emitting as UTF-8.
# (System nCad.lsp is plain ASCII in our installation; the comment header is
#  technically CP1251-encoded Russian, but Get/Set with -Encoding UTF8
#  roundtrips bytes since the comment area is all in the Russian range and
#  PowerShell's UTF8 mode does NOT detect/convert legacy ANSI. To be safe
#  we use raw bytes append instead.)
$origBytes = [System.IO.File]::ReadAllBytes($NanoCadSystemLsp)
$appendBytes = [System.Text.Encoding]::ASCII.GetBytes($block)  # our block is pure ASCII
$combined = New-Object byte[] ($origBytes.Length + $appendBytes.Length)
[Array]::Copy($origBytes, 0, $combined, 0, $origBytes.Length)
[Array]::Copy($appendBytes, 0, $combined, $origBytes.Length, $appendBytes.Length)
[System.IO.File]::WriteAllBytes($NanoCadSystemLsp, $combined)

Write-Host ""
Write-Host "Auto-NETLOAD hook installed."
Write-Host "  System file: $NanoCadSystemLsp"
Write-Host "  Backup:      $bak"
Write-Host "  User loader: $UserNcadLsp"
Write-Host ""
Write-Host "Next NanoCAD startup will auto-load the ClaudeBridge plugin."
Write-Host "Verify with:  curl http://127.0.0.1:7890/healthz"
