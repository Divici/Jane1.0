<#
.SYNOPSIS
    Installs Jane under %ProgramFiles% and registers it to start with Windows.

.DESCRIPTION
    Program Files is not a preference. uiAccess requires all three of a manifest that asks for it,
    a valid signature, and a binary in a "secure location" -- a directory ordinary users cannot
    write to. Installing anywhere else means Windows refuses to grant uiAccess, and, because the
    manifest asks for it, refuses to start the process at all.

    Reports honestly on the way out: if the binary is unsigned, the install still works and Jane
    still dictates; only injection into elevated windows is unavailable.
#>
[CmdletBinding()]
param(
    [string]$Source,

    [string]$Destination = (Join-Path $env:ProgramFiles 'Jane'),

    [switch]$NoAutostart,

    # Skips fetching the model runtime. Jane still dictates without it; only transcript cleanup
    # is unavailable, and Settings -> Models can fetch it later with one click.
    [switch]$NoModelRuntime,

    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repoRoot 'artifacts\publish' }

# One statement, one line. Windows PowerShell 5.1 does not continue an expression onto a line
# that begins with ".", so splitting this parses ".IsInRole(...)" as a command name and fails
# with "The term '.IsInRole' is not recognized". PowerShell 7 allows it; 5.1 is what ships.
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($currentIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Writing to $Destination requires elevation. Re-run this script from an elevated PowerShell."
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'Jane'

if ($Uninstall) {
    Get-Process -Name 'Jane' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    if (Test-Path $Destination) {
        Remove-Item $Destination -Recurse -Force
        Write-Host "Removed $Destination"
    }

    if (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $runKey -Name $runValue
        Write-Host 'Removed the autostart entry'
    }

    Write-Host ''
    Write-Host 'Jane is uninstalled. Two things are deliberately left behind:'
    Write-Host "  %LOCALAPPDATA%\Jane           settings, dictionary, and transcript history"
    Write-Host "  Cert:\LocalMachine\Root       the self-signed code-signing certificate"
    Write-Host 'Delete the first if you want your history gone. Remove the second by thumbprint if'
    Write-Host 'you are done with Jane entirely.'
    exit 0
}

if (-not (Test-Path (Join-Path $Source 'Jane.exe'))) {
    throw "Cannot find Jane.exe in $Source. Run build/publish.ps1 first."
}

Get-Process -Name 'Jane' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Installing to $Destination"
if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force

$installed = Join-Path $Destination 'Jane.exe'

if (-not $NoAutostart) {
    # HKCU, not HKLM: autostart is this user's preference, not a machine policy, and the tray's
    # own autostart toggle writes the same value so the two agree.
    New-ItemProperty -Path $runKey -Name $runValue -Value "`"$installed`"" -PropertyType String -Force | Out-Null
    Write-Host "Registered autostart: $runKey\$runValue"
}

# The model runtime goes under the user profile, not next to Jane.exe. Program Files is a
# "secure location" -- a uiAccess requirement -- so the account Jane runs as cannot write there,
# which means Jane could never fetch or update the runtime itself if it lived beside the binary.
# Shipping it inside the installer instead would add 1.4 GB to every download.
#
# This is the step whose absence produced the field report: the published build had no
# tools\ollama, so Jane's model server never started and both language-model rows read
# "Not downloaded" forever, next to a button that could not have worked.
$runtimeDir = Join-Path $env:LOCALAPPDATA 'Jane\tools\ollama'

if (-not $NoModelRuntime) {
    if (Test-Path (Join-Path $runtimeDir 'ollama.exe')) {
        Write-Host "Model runtime already present at $runtimeDir"
    }
    else {
        $bundled = Join-Path $Destination 'tools\ollama\ollama.exe'
        if (Test-Path $bundled) {
            Write-Host "Model runtime bundled with this build; leaving it in place."
        }
        else {
            Write-Host ''
            Write-Host "Fetching the model runtime into $runtimeDir"
            try {
                & (Join-Path $PSScriptRoot 'get-ollama.ps1') -Destination $runtimeDir
            }
            catch {
                Write-Warning @"
The model runtime could not be downloaded: $($_.Exception.Message)
Jane still dictates -- the recogniser emits punctuation and casing on its own. To add transcript
cleanup later, open Settings -> Models and use the Ollama runtime row.
"@
            }
        }
    }
}

$signature = Get-AuthenticodeSignature -FilePath $installed
Write-Host ''
Write-Host "Installed to $installed"
Write-Host "Signature: $($signature.Status)"

if ($signature.Status -eq 'Valid') {
    Write-Host ''
    Write-Host 'uiAccess is available: Jane can type into elevated windows.'
    Write-Host 'Note that the Windows secure desktop -- the UAC consent prompt itself, Ctrl+Alt+Del,'
    Write-Host 'and credential prompts -- remains out of reach. That is an OS boundary, not a gap.'
} else {
    Write-Warning @'
The binary is not validly signed, so Windows will refuse to START it while its manifest asks for
uiAccess. Either run build/sign-uiaccess.ps1 against this binary, or publish without
-p:JaneUiAccess=true and accept that elevated windows are out of reach.
'@
}

Write-Host ''
Write-Host 'Reboot, or start Jane now:'
Write-Host "  Start-Process '$installed'"
