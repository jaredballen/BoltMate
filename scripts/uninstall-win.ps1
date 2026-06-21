# Full BoltMate uninstall for development testing of the first-run flow.
#
# What this kills:
#   • running App + CLI processes
#   • scheduled-task autostart entries (App + CLI)
#   • on-disk settings (Roaming), logs (Local), caches, the lock file
#   • Windows Defender firewall rules so the network prompt re-fires
#
# Usage:
#   .\scripts\uninstall-win.ps1            # quit + remove state, leaves dev binaries
#   .\scripts\uninstall-win.ps1 -Hard      # also removes publish output under src\BoltMate.App\bin
#
# Run elevated only if you need to delete machine-wide scheduled tasks /
# firewall rules — per-user entries don't require admin.

[CmdletBinding()]
param(
    [switch]$Hard
)

$ErrorActionPreference = 'SilentlyContinue'
function Say($msg) { Write-Host "→ $msg" -ForegroundColor Cyan }

Say "Killing running processes"
Get-Process -Name 'BoltMate*' | Stop-Process -Force
Get-Process -Name 'boltmate'  | Stop-Process -Force
Start-Sleep -Seconds 1

Say "Removing scheduled tasks (Task Scheduler autostart)"
foreach ($name in 'BoltMate.App','BoltMate.Cli','BoltMate') {
    schtasks /delete /tn $name /f 2>&1 | Out-Null
}

Say "Removing on-disk state"
Remove-Item -Recurse -Force "$env:APPDATA\BoltMate"
Remove-Item -Recurse -Force "$env:LOCALAPPDATA\BoltMate"

Say "Removing Windows Defender firewall rules"
# Re-prompts the network dialog on next bind. Per-user + per-rule.
Remove-NetFirewallRule -DisplayName 'BoltMate*' 2>&1 | Out-Null

if ($Hard) {
    Say "Hard mode: removing publish output"
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Remove-Item -Recurse -Force (Join-Path $repoRoot 'src\BoltMate.App\bin')
    Remove-Item -Recurse -Force (Join-Path $repoRoot 'src\BoltMate.App\obj')
}

@"

Done. Notes:
  • Settings live in %APPDATA%\BoltMate (Roaming); logs in %LOCALAPPDATA%\BoltMate\Logs.
  • Firewall rules without admin only remove per-user entries; system rules
    require an elevated PowerShell. If the firewall prompt still doesn't
    appear, check Windows Defender Firewall → Allowed apps for stale entries.
  • Network "permission" on Win is the NLM profile category (Private vs
    Public). The welcome wizard probes that; toggling between profiles is
    done in Settings → Network & Internet.
"@ | Write-Host
