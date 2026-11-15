#Requires -Version 5.1
<#
.SYNOPSIS
  Authenticode-sign a published Update Express EXE (or dry-run).

.DESCRIPTION
  Called from BYGG-EXE.cmd after publish. Does not embed secrets.
  Configure via environment variables (see docs/code-signing-updateexpress.md
  in the project agent store, or repo README).

  Modes:
    - Unset / dry-run: print what would be signed and exit 0 (default until configured).
    - UE_SIGN_MODE=azure: SignTool + Azure Artifact Signing dlib + metadata JSON.
    - UE_SIGN_MODE=signtool: SignTool with certificate subject (/n) from a token/store.

  Set UE_SIGN=0 to skip. Set UE_SIGN_DRY_RUN=1 to force dry-run even when a mode is set.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-SignInfo([string] $Message) {
    Write-Host "[Sign-Release] $Message"
}

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $kitRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kitRoot)) { return $null }

    $found = Get-ChildItem -Path $kitRoot -Recurse -Filter 'signtool.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($found) { return $found.FullName }
    return $null
}

if (-not (Test-Path -LiteralPath $Path)) {
    throw "File not found: $Path"
}

$exe = (Resolve-Path -LiteralPath $Path).Path
$signEnabled = $env:UE_SIGN
if ($signEnabled -eq '0') {
    Write-SignInfo "Skipped (UE_SIGN=0): $exe"
    exit 0
}

$mode = if ($env:UE_SIGN_MODE) { $env:UE_SIGN_MODE.Trim().ToLowerInvariant() } else { '' }
$forceDryRun = ($env:UE_SIGN_DRY_RUN -eq '1') -or [string]::IsNullOrWhiteSpace($mode)

if ($forceDryRun) {
    $reason = if ($env:UE_SIGN_DRY_RUN -eq '1') {
        'UE_SIGN_DRY_RUN=1'
    } else {
        'no UE_SIGN_MODE configured'
    }
    Write-SignInfo "DRY-RUN: would sign $exe ($reason)"
    Write-SignInfo "Set UE_SIGN_MODE=azure|signtool after purchasing/validating a cert. See code-signing plan."
    exit 0
}

$signtool = Find-SignTool
if (-not $signtool) {
    throw 'signtool.exe not found. Install the Windows 10/11 SDK Signing Tools.'
}

$timestampUrl = if ($env:UE_SIGN_TIMESTAMP_URL) {
    $env:UE_SIGN_TIMESTAMP_URL
} elseif ($mode -eq 'azure') {
    'http://timestamp.acs.microsoft.com'
} else {
    'http://timestamp.digicert.com'
}

$args = @(
    'sign', '/v', '/fd', 'SHA256',
    '/tr', $timestampUrl, '/td', 'SHA256',
    '/d', 'Update Express'
)

switch ($mode) {
    'azure' {
        $dlib = $env:UE_SIGN_DLIB
        $metadata = $env:UE_SIGN_METADATA
        if ([string]::IsNullOrWhiteSpace($dlib) -or -not (Test-Path -LiteralPath $dlib)) {
            throw 'UE_SIGN_MODE=azure requires UE_SIGN_DLIB pointing at Azure.CodeSigning.Dlib.dll'
        }
        if ([string]::IsNullOrWhiteSpace($metadata) -or -not (Test-Path -LiteralPath $metadata)) {
            throw 'UE_SIGN_MODE=azure requires UE_SIGN_METADATA pointing at metadata.json (no secrets)'
        }
        if ($dlib -match ' ' -or $metadata -match ' ') {
            throw 'UE_SIGN_DLIB and UE_SIGN_METADATA paths must not contain spaces (SignTool dlib limitation). Prefer C:\azsign\...'
        }
        $args += @('/dlib', $dlib, '/dmdf', $metadata)
    }
    'signtool' {
        $subject = $env:UE_SIGN_CERT_SUBJECT
        $sha1 = $env:UE_SIGN_CERT_SHA1
        if (-not [string]::IsNullOrWhiteSpace($sha1)) {
            $args += @('/sha1', $sha1)
        } elseif (-not [string]::IsNullOrWhiteSpace($subject)) {
            $args += @('/n', $subject)
        } else {
            throw 'UE_SIGN_MODE=signtool requires UE_SIGN_CERT_SUBJECT or UE_SIGN_CERT_SHA1'
        }
    }
    default {
        throw "Unknown UE_SIGN_MODE='$mode' (use azure or signtool)"
    }
}

$args += $exe
Write-SignInfo "Signing with mode=$mode via $signtool"
& $signtool @args
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE"
}

$sig = Get-AuthenticodeSignature -FilePath $exe
Write-SignInfo "Status=$($sig.Status); Publisher=$($sig.SignerCertificate.Subject)"
if ($sig.Status -ne 'Valid') {
    throw "Authenticode status is $($sig.Status), expected Valid"
}

exit 0
