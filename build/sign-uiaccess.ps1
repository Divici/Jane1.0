<#
.SYNOPSIS
    Generates a self-signed code-signing certificate, signs Jane with it, then removes the private
    key from the machine.

.DESCRIPTION
    uiAccess requires a signed binary whose certificate chains to a root the machine trusts. A
    purchased certificate would be the ordinary answer; the user's constraint is zero spend, and a
    self-signed root is legitimate for a single personal machine.

    It is also a real risk, and the mitigations here are not optional decoration:

    * The certificate is constrained to the code-signing EKU (1.3.6.1.5.5.7.3.3) and nothing else.
      An unconstrained root in Trusted Root Certification Authorities would let anything it signed
      be trusted for TLS, for EFS, for anything at all.

    * The private key is exported to a PFX you keep offline and is then DELETED from the machine's
      certificate stores. Only the public certificate stays behind, which is all that is needed to
      validate the signature. A machine-wide trusted root whose private key is sitting in the
      current user's store is a key that malware can use to sign whatever it likes.

    * A long validity period is used instead of a timestamp authority. A timestamp would be
      better, and any free RFC-3161 authority works -- pass -TimestampUrl to use one. Without it,
      the signature stops validating when the certificate expires.

    Signing is optional. If you skip it, Jane runs fully; only injection into elevated windows is
    unavailable, and settings says so plainly.

.EXAMPLE
    ./build/sign-uiaccess.ps1 -Path artifacts\publish\Jane.exe -PfxPath D:\keys\jane-signing.pfx

    First time. Generates a certificate, trusts it, signs, and exports the key.

.EXAMPLE
    ./build/sign-uiaccess.ps1 -Path artifacts\publish\Jane.exe -PfxPath D:\keys\jane-signing.pfx -ReuseKey

    Every time after that. A new build has to be re-signed because the signature covers the
    binary; -ReuseKey signs with the certificate the machine already trusts instead of asking it
    to trust another one.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    # Where the exported private key is written. Keep this off the machine that trusts it.
    [string]$PfxPath,

    [string]$Subject = 'CN=Jane Local Dictation (self-signed)',

    [int]$ValidYears = 10,

    # Any free RFC-3161 authority, e.g. http://timestamp.digicert.com
    [string]$TimestampUrl,

    # Sign with the key already exported to -PfxPath instead of generating a new certificate.
    #
    # This is what you want for every update after the first. The signature covers the binary, so
    # a new build has to be re-signed; generating a fresh certificate each time would ask the
    # machine to trust one more self-signed root per update, and each of those roots is a key that
    # can sign anything at all. Reusing the key keeps that number at one forever.
    [switch]$ReuseKey,

    # Keep the private key in the store. Off by default, and you should leave it off.
    [switch]$KeepPrivateKey,

    # Generate and sign, but do not touch the machine-wide trust store. The signature will not
    # validate -- nothing trusts the certificate -- but the certificate generation, EKU
    # constraint, export and signing mechanics are all exercised. Used to verify this script
    # without making an irreversible change to what the machine trusts.
    [switch]$SkipTrustStore,

    # Supplied instead of prompting, so the script can run unattended.
    [System.Security.SecureString]$PfxPassword
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $Path)) { throw "Cannot find $Path" }
$Path = (Resolve-Path $Path).Path

if (-not $SkipTrustStore) {
    # One statement, one line. Windows PowerShell 5.1 does not continue an expression onto a line
    # that begins with ".", so splitting this parses ".IsInRole(...)" as a command name and fails
    # with "The term '.IsInRole' is not recognized". PowerShell 7 allows it; 5.1 is what ships.
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object Security.Principal.WindowsPrincipal($currentIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'This script installs a certificate into the local machine Trusted Root store and must run elevated. Pass -SkipTrustStore to exercise everything except that.'
    }
}

if (-not $PfxPath) {
    $PfxPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'jane-signing-key.pfx'
}

if ($ReuseKey) {
    if (-not (Test-Path $PfxPath)) {
        throw "-ReuseKey needs the key that was exported when the certificate was created, and there is no file at $PfxPath. Point -PfxPath at it, or drop -ReuseKey to generate a new certificate (which then has to be trusted, in addition to the one already trusted)."
    }

    Write-Host "Reusing the code-signing certificate in $PfxPath"

    $password = if ($PfxPassword) { $PfxPassword } else {
        Read-Host -AsSecureString -Prompt 'Password for the exported private key'
    }

    # Into CurrentUser\My rather than loaded as an X509Certificate2, because Authenticode signing
    # on Windows PowerShell 5.1 wants a key with a CSP handle behind it. The finally block at the
    # bottom removes it again, exactly as it does for a freshly generated one.
    $certificate = Import-PfxCertificate `
        -FilePath $PfxPath `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -Password $password
} else {
    Write-Host "Generating a code-signing certificate: $Subject"

    # TextExtension pins the EKU to code signing only. Without it New-SelfSignedCertificate produces
    # a certificate valid for far more than signing one binary.
    $certificate = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyLength 3072 `
        -KeyAlgorithm RSA `
        -HashAlgorithm SHA256 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($ValidYears) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}

$thumbprint = $certificate.Thumbprint
Write-Host "  thumbprint $thumbprint"
Write-Host "  EKU        $(($certificate.EnhancedKeyUsageList | ForEach-Object { $_.FriendlyName }) -join ', ')"

if (-not $ReuseKey) {
    # Export the private key BEFORE it is deleted. Losing it means generating a new certificate and
    # trusting that one too, which is survivable but leaves two roots where one would do.
    $password = if ($PfxPassword) { $PfxPassword } else {
        Read-Host -AsSecureString -Prompt 'Password to protect the exported private key'
    }
    Export-PfxCertificate -Cert $certificate -FilePath $PfxPath -Password $password | Out-Null
    Write-Host "  private key exported to $PfxPath -- move this off this machine"
}

if ($SkipTrustStore) {
    Write-Host '  -SkipTrustStore: machine-wide trust is left untouched, so the signature will not validate'
} else {
    # Only the public certificate goes into the machine's trusted root. This is what makes the
    # signature validate; it does not require the private key.
    $publicPath = Join-Path ([IO.Path]::GetTempPath()) "jane-signing-$thumbprint.cer"
    Export-Certificate -Cert $certificate -FilePath $publicPath | Out-Null
    Import-Certificate -FilePath $publicPath -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Import-Certificate -FilePath $publicPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
    Remove-Item $publicPath -Force
    Write-Host '  public certificate installed into LocalMachine\Root and \TrustedPublisher'
}

try {
    Write-Host "Signing $Path"
    $signArguments = @{
        FilePath      = $Path
        Certificate   = $certificate
        HashAlgorithm = 'SHA256'
    }
    if ($TimestampUrl) { $signArguments['TimestampServer'] = $TimestampUrl }

    $signature = Set-AuthenticodeSignature @signArguments
    if ($signature.Status -ne 'Valid' -and -not $SkipTrustStore) {
        throw "Signing failed: $($signature.Status) - $($signature.StatusMessage)"
    }

    Write-Host "  signature status: $($signature.Status)"
}
finally {
    # In a finally, because a signing failure is exactly the moment a forgotten private key is
    # most likely: the script stops early, the key stays in the store, and the operator moves on.
    # A machine-wide trusted root whose private key is still on the machine is a key that anything
    # running as this user can sign with.
    if (-not $KeepPrivateKey) {
        Get-ChildItem 'Cert:\CurrentUser\My' |
            Where-Object { $_.Thumbprint -eq $thumbprint } |
            Remove-Item -Force
        Write-Host '  private key removed from Cert:\CurrentUser\My'
    }
}

Write-Host ''
Write-Host 'Signed. uiAccess additionally requires the binary to run from a secure location:'
Write-Host '  ./build/install.ps1'
Write-Host ''
if (-not $TimestampUrl) {
    # The certificate's own expiry, not now-plus-ValidYears: on the -ReuseKey path the certificate
    # was issued at some point in the past and -ValidYears had nothing to do with it.
    Write-Warning "No timestamp authority was used, so this signature stops validating on $($certificate.NotAfter.ToString('yyyy-MM-dd')). Pass -TimestampUrl to avoid that."
}
