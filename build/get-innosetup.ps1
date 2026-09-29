<#
.SYNOPSIS
    Downloads Inno Setup, verifies it, and unpacks it into tools/innosetup.

.DESCRIPTION
    Inno Setup is the program that turns a published Jane into JaneSetup.exe. It is fetched
    rather than assumed, for the same reason get-ollama.ps1 exists: a build that depends on
    whatever happens to be installed is a build that works on one machine.

    The download is checked against a SHA-256 pinned in this file before it is run. It is then
    installed per-user and in portable mode into tools/innosetup, which is gitignored: nothing is
    written to Program Files, and no administrator is needed.

    Idempotent: skips everything when the requested version is already unpacked, unless -Force
    is given.
#>
[CmdletBinding()]
param(
    # Pinned so the toolchain is reproducible. Bump deliberately, together with the hash.
    [string]$Version = '6.7.3',

    [string]$Sha256 = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732',

    [string]$Destination,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot 'tools\innosetup' }

$tag = 'is-' + $Version.Replace('.', '_')
$asset = "innosetup-$Version.exe"
$url = "https://github.com/jrsoftware/issrc/releases/download/$tag/$asset"
$stamp = Join-Path $Destination '.version'
$compiler = Join-Path $Destination 'ISCC.exe'

if ((Test-Path $compiler) -and (Test-Path $stamp) -and -not $Force) {
    $have = (Get-Content $stamp -Raw).Trim()
    if ($have -eq $Version) {
        Write-Host "Inno Setup $Version already present at $Destination"
        exit 0
    }
    Write-Host "Replacing Inno Setup $have with $Version"
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('jane-innosetup-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$installer = Join-Path $tempDir $asset

try {
    Write-Host "Downloading $url"
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing }
    finally { $ProgressPreference = $previous }

    $actual = (Get-FileHash -Path $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "Inno Setup $Version failed verification. Expected SHA-256 $Sha256 but the download is $actual. Nothing was run."
    }
    Write-Host "Verified SHA-256 $actual"

    if (Test-Path $Destination) { Remove-Item -Path $Destination -Recurse -Force }

    # Portable and per-user: no uninstall entry, no file association, no Start menu folder, and
    # no elevation. The compiler is all that is wanted from it.
    $arguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/CURRENTUSER',
        '/PORTABLE=1',
        '/NOICONS',
        '/TASKS=""',
        "/DIR=`"$Destination`""
    )

    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "The Inno Setup installer exited with code $($process.ExitCode)."
    }

    if (-not (Test-Path $compiler)) {
        throw "Inno Setup installed but $compiler is missing -- the layout changed upstream."
    }

    Set-Content -Path $stamp -Value $Version -Encoding utf8
    Write-Host "Inno Setup $Version is ready at $Destination"
}
finally {
    if (Test-Path $tempDir) { Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
}
