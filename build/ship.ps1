<#
.SYNOPSIS
    Rebuilds Jane, re-signs it, and reinstalls it -- the three steps of an update, as one command.

.DESCRIPTION
    Publish, sign and install are separate scripts because they answer to different concerns: an
    unsigned publish is still a useful artefact, and signing touches the machine-wide trust store.
    That separation is right for the first install and wrong for every update afterwards, where
    the three always run together and typing them out invites getting one wrong -- most often
    forgetting -ReuseKey, which asks the machine to trust one more self-signed root.

    Everything here is a thin wrapper. Anything that fails, fails in the underlying script with
    its own message.

.PARAMETER PfxPath
    The exported signing key from the first run of build/sign-uiaccess.ps1. Defaults to
    JANE_SIGNING_PFX, so the usual update is `./build/ship.ps1` with no arguments at all.

.PARAMETER SkipSigning
    Publish and install without signing. Jane dictates fully unsigned; only injection into
    elevated windows is unavailable.

.EXAMPLE
    ./build/ship.ps1
    The ordinary update, with the key found through JANE_SIGNING_PFX.

.EXAMPLE
    ./build/ship.ps1 -PfxPath D:\jane-signing-key.pfx
    The same, naming the key explicitly.
#>
[CmdletBinding()]
param(
    [string]$PfxPath = $env:JANE_SIGNING_PFX,

    [switch]$IncludeOllama,

    [switch]$SkipSigning,

    [switch]$NoAutostart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot
$repoRoot = Split-Path -Parent $here

# Checked before the publish rather than after it. Discovering the key is missing at the signing
# step means doing the whole build again.
if (-not $SkipSigning) {
    if (-not $PfxPath) {
        Write-Host 'No signing key.' -ForegroundColor Red
        Write-Host 'Pass -PfxPath, or set it once so every future update needs no arguments:'
        Write-Host ''
        Write-Host "    [Environment]::SetEnvironmentVariable('JANE_SIGNING_PFX', 'D:\jane-signing-key.pfx', 'User')"
        Write-Host ''
        Write-Host 'Or pass -SkipSigning to install unsigned, which costs only injection into'
        Write-Host 'elevated windows.'
        throw 'No signing key.'
    }

    if (-not (Test-Path $PfxPath)) {
        throw "The signing key '$PfxPath' does not exist. Pass -SkipSigning to install unsigned."
    }
}

# One statement, one line: Windows PowerShell 5.1 will not continue an expression onto a line that
# begins with ".", which is why this is not split for width.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Installing to Program Files requires elevation. Re-run this from an elevated PowerShell.'
}

$publish = Join-Path $repoRoot 'artifacts\publish'
$exe = Join-Path $publish 'Jane.exe'

Write-Host ''
Write-Host '[1/3] Publishing' -ForegroundColor Cyan
& (Join-Path $here 'publish.ps1') -IncludeOllama:$IncludeOllama

if ($SkipSigning) {
    Write-Host ''
    Write-Host '[2/3] Signing skipped -- uiAccess will be unavailable' -ForegroundColor DarkYellow
}
else {
    Write-Host ''
    Write-Host '[2/3] Signing' -ForegroundColor Cyan
    & (Join-Path $here 'sign-uiaccess.ps1') -Path $exe -PfxPath $PfxPath -ReuseKey
}

Write-Host ''
Write-Host '[3/3] Installing' -ForegroundColor Cyan
& (Join-Path $here 'install.ps1') -Source $publish -NoAutostart:$NoAutostart

Write-Host ''
Write-Host 'Jane is updated and running the new build.' -ForegroundColor Green
