# Install ClaudeTaskBoard: copy the exe to LocalAppData, register its Claude Code hook, create a desktop
# shortcut, and launch it.
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

# 4. Desktop shortcut (re-created each run so it always points at the installed copy).
$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Task Board.lnk'
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sc.TargetPath = $installed
$sc.WorkingDirectory = $dest
$sc.IconLocation = "$installed,0"
$sc.Description = 'Claude Code のセッション・タスク・確認をフロート表示'
$sc.Save()

# 5. Explorer right-click menu: "Claude Code で開く" (opens the folder in the desktop app).
& (Join-Path $root 'tools\context-menu.ps1')

# A fresh install is a deliberate start: clear an earlier "closed by user" so self-healing is back on.
$hb = Join-Path $env:USERPROFILE '.claude\taskboard\board.json'
if (Test-Path $hb) { Remove-Item $hb -Force }

Start-Process $installed
Write-Host "Installed to $installed and launched. Hook registered. Shortcut: $lnk"
