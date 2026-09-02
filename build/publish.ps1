<#
.SYNOPSIS
    Publishes Jane as a single self-contained win-x64 binary, with the uiAccess manifest.

.DESCRIPTION
    Two things differ from an ordinary `dotnet publish`:

    1. -p:JaneUiAccess=true selects app.uiaccess.manifest instead of app.manifest. Development
       builds must NOT use it: Windows refuses to start an unsigned uiAccess binary from outside a
       secure location, so a debug build carrying it cannot be launched at all.

    2. The output is staged with NOTICE.md and, optionally, tools/ollama, because the installer
       copies a directory rather than a file.

    Signing is a separate step (build/sign-uiaccess.ps1) so that an unsigned publish is still a
    useful artefact: Jane runs fully without uiAccess, and only injection into elevated windows is
    unavailable.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputDirectory,

    # Bundle the standalone ollama.exe alongside Jane. Off by default: it is 1.4 GB, and a user
    # who already ran build/get-ollama.ps1 does not need a second copy.
    [switch]$IncludeOllama,

    [switch]$SelfContained = $true
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\publish' }

$project = Join-Path $repoRoot 'src\Jane.App\Jane.App.csproj'
if (-not (Test-Path $project)) { throw "Cannot find $project" }

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Write-Host "Publishing Jane ($Configuration, win-x64, self-contained=$SelfContained) to $OutputDirectory"

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', 'win-x64',
    "--self-contained=$($SelfContained.ToString().ToLowerInvariant())",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:JaneUiAccess=true',
    '-p:DebugType=embedded',
    '-o', $OutputDirectory
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $OutputDirectory 'Jane.exe'
if (-not (Test-Path $exe)) { throw "Publish completed but $exe is missing." }

# The licence attributions ship beside the binary, not only inside the About view. Parakeet is
# CC-BY-4.0, which makes this an obligation rather than a courtesy.
$notice = Join-Path $repoRoot 'NOTICE.md'
if (Test-Path $notice) {
    Copy-Item $notice -Destination $OutputDirectory
} else {
    Write-Warning 'NOTICE.md is missing. Jane ships model weights under CC-BY-4.0 and MIT; attribution is required.'
}

if ($IncludeOllama) {
    $source = Join-Path $repoRoot 'tools\ollama'
    if (Test-Path $source) {
        Write-Host 'Bundling tools/ollama'
        Copy-Item $source -Destination (Join-Path $OutputDirectory 'tools\ollama') -Recurse -Force
    } else {
        Write-Warning 'tools/ollama is not present. Run build/get-ollama.ps1 first, or omit -IncludeOllama.'
    }
}

$size = [math]::Round(((Get-ChildItem $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ''
Write-Host "Published $size MB to $OutputDirectory"
Write-Host ''
Write-Host 'This binary declares uiAccess="true". Windows will refuse to start it until it is'
Write-Host 'both signed and installed under %ProgramFiles%. Next:'
Write-Host "  ./build/sign-uiaccess.ps1 -Path '$exe'"
Write-Host '  ./build/install.ps1'
