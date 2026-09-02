<#
.SYNOPSIS
    Downloads the standalone ollama.exe archive and unpacks it into tools/ollama.

.DESCRIPTION
    Jane deliberately does NOT use the winget desktop package (Ollama.Ollama). That
    package installs a tray service which owns :11434, autostarts with Windows and
    auto-updates over the network -- all three break Jane's supervision model and its
    zero-egress promise. The official release archive is a plain ollama.exe plus its
    runner DLLs; Jane starts it as a supervised child process on a non-default port.

    Idempotent: skips the download when the requested version is already unpacked,
    unless -Force is given.
#>
[CmdletBinding()]
param(
    # Pinned so the toolchain is reproducible. Bump deliberately, never automatically.
    [string]$Version = 'v0.33.2',
    [string]$Destination,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Destination) { $Destination = Join-Path $repoRoot 'tools\ollama' }

$asset = 'ollama-windows-amd64.zip'
$url = "https://github.com/ollama/ollama/releases/download/$Version/$asset"
$stamp = Join-Path $Destination '.version'
$exe = Join-Path $Destination 'ollama.exe'

if ((Test-Path $exe) -and (Test-Path $stamp) -and -not $Force) {
    $have = (Get-Content $stamp -Raw).Trim()
    if ($have -eq $Version) {
        Write-Host "ollama $Version already present at $Destination"
        & $exe --version
        exit 0
    }
    Write-Host "Replacing ollama $have with $Version"
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("jane-ollama-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$zip = Join-Path $tempDir $asset

try {
    Write-Host "Downloading $url"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    # ProgressPreference=SilentlyContinue makes Invoke-WebRequest roughly 10x faster
    # on large files (the progress bar repaint dominates otherwise).
    $prev = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing }
    finally { $ProgressPreference = $prev }
    $sw.Stop()

    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Downloaded $mb MB in $([math]::Round($sw.Elapsed.TotalSeconds,1))s"

    $sha = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "SHA-256 $sha"

    if (Test-Path $Destination) { Remove-Item -Path $Destination -Recurse -Force }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    Write-Host "Extracting to $Destination"
    Expand-Archive -Path $zip -DestinationPath $Destination -Force

    if (-not (Test-Path $exe)) {
        throw "Archive extracted but $exe is missing -- asset layout changed upstream."
    }

    Set-Content -Path $stamp -Value $Version -Encoding utf8
    Set-Content -Path (Join-Path $Destination '.sha256') -Value $sha -Encoding utf8

    Write-Host ''
    & $exe --version
    Write-Host ''
    Write-Host "Next: $exe pull qwen3:4b   and   $exe pull qwen3:1.7b"
}
finally {
    if (Test-Path $tempDir) { Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue }
}
