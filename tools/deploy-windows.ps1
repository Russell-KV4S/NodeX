<#
.SYNOPSIS
  Publishes the Windows desktop app (self-contained, win-x64, Release) and copies it to C:\Program Files\NodeX.

.EXAMPLE
  pwsh tools\deploy-windows.ps1
  pwsh tools\deploy-windows.ps1 -Destination 'D:\Apps\NodeX' -StopRunning

.NOTES
  Settings and secrets live in %LOCALAPPDATA%\NodeX and are never touched.
  Writing to Program Files needs an elevated shell on most machines; the script says so if the copy is denied.
#>
param(
    [string]$Destination = 'C:\Program Files\NodeX',
    [switch]$StopRunning   # close a running NodeX first instead of refusing
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out  = Join-Path ([IO.Path]::GetTempPath()) 'nodex-win-publish'

$running = Get-Process -Name 'NodeX.UI.Desktop' -ErrorAction SilentlyContinue
if ($running) {
    if (-not $StopRunning) { throw 'NodeX is running. Close it, or re-run with -StopRunning.' }
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $repo 'src\NodeX.UI\NodeX.UI.Desktop') -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
try {
    Copy-Item -Path (Join-Path $out '*') -Destination $Destination -Recurse -Force
}
catch [System.UnauthorizedAccessException] {
    throw "Access denied writing to $Destination. Re-run from an elevated (Administrator) PowerShell."
}

$a = (Get-FileHash (Join-Path $out 'NodeX.UI.dll')).Hash
$b = (Get-FileHash (Join-Path $Destination 'NodeX.UI.dll')).Hash
if ($a -ne $b) { throw 'Copy finished but NodeX.UI.dll differs from the build.' }
Write-Host "Deployed to $Destination (NodeX.UI.dll hash matches the build)."
