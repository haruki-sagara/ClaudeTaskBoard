# Add (or with -Uninstall remove) "Claude Code で開く" to Explorer's folder right-click menu, for the current
# user only. It starts a new Claude Code session on that folder in the desktop app. Re-runnable.
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$keys = @(
    'HKCU:\Software\Classes\Directory\shell\ClaudeCode',            # right-click on a folder
    'HKCU:\Software\Classes\Directory\Background\shell\ClaudeCode'  # right-click inside a folder
)

if ($Uninstall) {
    foreach ($k in $keys) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    Write-Host 'Removed the Claude Code entry from the right-click menu.'
    return
}

$exe = Join-Path $env:LOCALAPPDATA 'ClaudeTaskBoard\ClaudeTaskBoard.exe'
if (-not (Test-Path $exe)) { throw "$exe not found. Run install.ps1 first." }

# Spelled as char codes so the label survives Windows PowerShell 5 reading this file as ANSI.
$label = 'C&laude Code ' + (-join [char[]](0x3067, 0x958B, 0x304F))
# The exe URL-encodes the path into a claude:// link, which a registry command cannot do by itself.
# "%V." rather than "%V": on a drive root "C:\" would otherwise escape the closing quote.
$command = "`"$exe`" --open-folder `"%V.`""

foreach ($k in $keys) {
    New-Item -Path $k -Force -Value $label | Out-Null
    New-ItemProperty -Path $k -Name 'Icon' -Value $exe -PropertyType String -Force | Out-Null
    New-Item -Path "$k\command" -Force -Value $command | Out-Null
}
Write-Host "Registered: $label -> $command"
