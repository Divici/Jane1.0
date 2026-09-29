<#
.SYNOPSIS
    Builds JaneSetup.exe, the installer published on the releases page.

.DESCRIPTION
    Three steps:

    1. Publish Jane WITHOUT the uiAccess manifest. The public build is unsigned, and Windows will
       not start an unsigned binary that asks for uiAccess. This is a different artefact from the
       one build/ship.ps1 produces for a machine that trusts your own certificate, and it goes to
       a different directory so neither overwrites the other.

    2. Compile build/installer/Jane.iss with Inno Setup.

    3. Write the installer's SHA-256 beside it, so a release can say what it published.

    The language-model runtime is deliberately not bundled. It is 1.4 GB, and Jane fetches it
    itself the first time it is wanted.
#>
[CmdletBinding()]
param(
    # Defaults to <Version> in Directory.Build.props. The release workflow passes the tag.
    [string]$Version,

    [string]$PublishDirectory,

    [string]$OutputDirectory,

    # Reuse an existing publish rather than building again.
    [switch]$SkipPublish,

    # Installs under a separate identity and leaves a running Jane alone. For trying the
    # installer on a machine where Jane is already in use.
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repoRoot 'artifacts\publish-setup' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\setup' }

# Inno Setup resolves a relative path against the folder the script is in, not the folder the
# command was run from, so "-OutputDirectory out" would quietly write somewhere else entirely.
$here = (Get-Location).ProviderPath
if (-not [System.IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $here $PublishDirectory }
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $here $OutputDirectory }
$PublishDirectory = [System.IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not $Version) {
    $props = Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw
    $match = [regex]::Match($props, '<Version>([^<]+)</Version>')
    if (-not $match.Success) { throw 'Directory.Build.props has no <Version>, and -Version was not given.' }
    $Version = $match.Groups[1].Value
}

# A tag arrives as v1.2.3; an installer version is 1.2.3.
$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "'$Version' is not a version Inno Setup accepts. Expected something like 1.2.3."
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $PublishDirectory -NoUiAccess -Version $Version
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $PublishDirectory 'Jane.exe'))) {
    throw "Cannot find Jane.exe in $PublishDirectory. Run without -SkipPublish."
}

$candidates = @(
    (Join-Path $repoRoot 'tools\innosetup\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
)

$compiler = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $compiler) {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $compiler = $onPath.Source }
}

if (-not $compiler) {
    throw 'Inno Setup was not found. Run build/get-innosetup.ps1 first.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$arguments = @(
    "/DAppVersion=$Version",
    "/DSourceDir=$PublishDirectory",
    "/DOutputDir=$OutputDirectory",
    '/Qp'
)
if ($SmokeTest) { $arguments += '/DSmokeTest' }
$arguments += (Join-Path $PSScriptRoot 'installer\Jane.iss')

Write-Host "Compiling the installer with $compiler"
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

$setup = Join-Path $OutputDirectory 'JaneSetup.exe'
if (-not (Test-Path $setup)) { throw "Inno Setup finished but $setup is missing." }

$hash = (Get-FileHash -Path $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setup.sha256" -Value "$hash  JaneSetup.exe" -Encoding ascii

$size = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host ''
Write-Host "Built $setup"
Write-Host "  version  $Version"
Write-Host "  size     $size MB"
Write-Host "  sha-256  $hash"
Write-Host ''
Write-Host 'This installer is unsigned. Windows SmartScreen will warn whoever runs it, and the'
Write-Host 'installed Jane cannot type into windows that are running as administrator.'
