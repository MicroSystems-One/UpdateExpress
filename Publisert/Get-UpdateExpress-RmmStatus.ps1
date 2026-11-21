<#
.SYNOPSIS
  Read Update Express rmm-status for N-able custom properties / script output.

.NOTES
  Looks in ProgramData first, then the per-user InstallDir.
  Exit 0 always when a file is found; exit 1 if missing.
#>
[CmdletBinding()]
param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$candidates = @(
    (Join-Path $env:ProgramData 'MicroSystems\Update Express\rmm-status.json'),
    (Join-Path $env:ProgramData 'MicroSystems\Update Express\rmm-status.txt'),
    (Join-Path $env:LOCALAPPDATA 'MicroSystems\Update Express\rmm-status.json'),
    (Join-Path $env:LOCALAPPDATA 'MicroSystems\Update Express\rmm-status.txt')
)

$path = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $path) {
    Write-Output 'ue.status=missing'
    exit 1
}

if ($Json -or $path.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
    if ($path.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) {
        $jsonSibling = [IO.Path]::ChangeExtension($path, '.json')
        if (Test-Path $jsonSibling) { $path = $jsonSibling }
    }
}

Write-Output "ue.statusFile=$path"
Get-Content -LiteralPath $path -Raw
exit 0
