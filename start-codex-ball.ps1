[CmdletBinding()]
param(
  [ValidateRange(1, 65535)]
  [int]$Port = 8765,

  [string]$ThreadId,

  [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$serverPath = Join-Path $projectRoot 'bridge\server.mjs'
$pageUri = "http://127.0.0.1:$Port/codex.html"
$healthUri = "http://127.0.0.1:$Port/api/health"
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue

if (-not $nodeCommand) {
  throw 'Node.js was not found in PATH. Install Node.js 18 or newer first.'
}

try {
  $health = Invoke-RestMethod -Uri $healthUri -TimeoutSec 1
  if ($health.ok -eq $true) {
    Write-Host "Emotion Ball Codex bridge is already running: $pageUri"
    if (-not $NoOpen) { Start-Process $pageUri }
    exit 0
  }
} catch {
  # No bridge is listening on this port yet; start one below.
}

$arguments = @($serverPath, '--port', [string]$Port)
if ($ThreadId) { $arguments += @('--thread-id', $ThreadId) }

if (-not $NoOpen) {
  Start-Process $pageUri
}

Write-Host "Emotion Ball Codex bridge is starting: $pageUri"
Write-Host 'Keep this window open. Press Ctrl+C to stop the bridge.'
& $nodeCommand.Source @arguments
