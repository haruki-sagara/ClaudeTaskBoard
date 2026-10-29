# Remove ClaudeTaskBoard: unregister its hook, stop it, drop autostart, delete the installed copy.
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

if (Get-Command node -ErrorAction SilentlyContinue) { node (Join-Path $root 'hooks\uninstall.js') }
Get-Process ClaudeTaskBoard -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ClaudeTaskBoard' -ErrorAction SilentlyContinue
$dest = Join-Path $env:LOCALAPPDATA 'ClaudeTaskBoard'
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }

Write-Host "Uninstalled. (State and settings under ~\.claude\taskboard were left; delete manually if desired.)"
