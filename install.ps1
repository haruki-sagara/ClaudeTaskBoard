# Install ClaudeTaskBoard: copy the exe to LocalAppData, register its Claude Code hook, and launch it.
# Standalone — needs nothing from claude-status-tray. Re-runnable.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw "Node.js is required (the hook runs under it). Get it from https://nodejs.org and re-run."
}
$exe = Join-Path $root 'dist\ClaudeTaskBoard.exe'
if (-not (Test-Path $exe)) {
    throw "dist\ClaudeTaskBoard.exe not found. Build first: dotnet publish .\ClaudeTaskBoard.csproj -c Release -p:PublishSingleFile=true -o .\dist"
}

# 1. Copy into a stable location (dist\ is a build dir that gets overwritten).
$dest = Join-Path $env:LOCALAPPDATA 'ClaudeTaskBoard'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$installed = Join-Path $dest 'ClaudeTaskBoard.exe'
Get-Process ClaudeTaskBoard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500   # let the file handle go before overwriting
Copy-Item $exe $installed -Force

# 2. Register the hook in ~/.claude/settings.json (merged, backed up, paths quoted).
node (Join-Path $root 'hooks\install.js')

# 3. Point an existing autostart entry at the installed copy (the in-app switch creates it).
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $run -Name 'ClaudeTaskBoard' -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $run -Name 'ClaudeTaskBoard' -Value "`"$installed`""
}

Start-Process $installed
Write-Host "Installed to $installed and launched. Hook registered."
