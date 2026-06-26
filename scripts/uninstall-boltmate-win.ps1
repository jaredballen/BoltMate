# uninstall-boltmate-win.ps1 — scrub a BoltMate install on the Win VM.
#
# Invoked from release-win.sh between builds. Idempotent: silently
# succeeds when nothing to clean.
#
# What gets removed:
#   - Running BoltMate.exe process
#   - Per-user Inno Setup unins000.exe (graceful uninstall)
#   - Stranded install dir
#   - Start Menu shortcut group
#   - HKCU AppUserModelId entries that point at BoltMate (display
#     name match OR path-derived key OR matching activator GUID)
#   - HKCU CLSID entries for each CustomActivator referenced above
#   - HKCU Notifications\Settings entries for the same AUMIDs
#   - %APPDATA%\BoltMate\settings.json
#   - %LOCALAPPDATA%\Microsoft\WindowsAppSDK\* matching our CLSID(s)
#   - Defender Firewall rules tagged with the BoltMate display name
#
# Why the AppUserModelId / CLSID scrub matters:
#   WinAppSDK Register() writes
#     HKCU\Software\Classes\AppUserModelId\<aumid> = { DisplayName,
#       IconUri, CustomActivator(=CLSID) }
#     HKCU\Software\Classes\CLSID\<CLSID>\LocalServer32
#   A stale entry from a prior build (different CLSID, different exe
#   path) makes the OS notification platform return
#   DisabledForApplication for our AUMID — toasts post but no banner
#   ever surfaces. Each build iteration must start from a clean slate
#   so Register() can write a coherent set.

[CmdletBinding()]
param()

function Remove-RegKey($path) {
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path -EA SilentlyContinue
    }
}

Write-Host '==> Stopping BoltMate process'
Get-Process BoltMate -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$installRoot = "$env:LOCALAPPDATA\Programs\BoltMate"
$uninstaller = Join-Path $installRoot 'unins000.exe'
if (Test-Path $uninstaller) {
    Write-Host '==> Running unins000.exe (silent)'
    Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT','/NORESTART' -Wait
    Start-Sleep -Seconds 3
} else {
    Write-Host '==> unins000.exe not present — nothing to gracefully uninstall'
}

Write-Host '==> Scrubbing install dir + shortcuts + settings + firewall'
Remove-Item -Recurse -Force $installRoot -EA SilentlyContinue

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\BoltMate'
Remove-Item -Recurse -Force $startMenu -EA SilentlyContinue

$settings = Join-Path $env:APPDATA 'BoltMate\settings.json'
Remove-Item -Force $settings -EA SilentlyContinue

Remove-NetFirewallRule -DisplayName BoltMate* -EA SilentlyContinue

Write-Host '==> Scrubbing AppUserModelId entries that reference BoltMate'
# Walk HKCU\Software\Classes\AppUserModelId looking for:
#   • subkey name == 'BoltMate'
#   • subkey name containing 'boltmate' (case-insensitive, catches path-
#     derived stale entries like c:.users.jallen...boltmate.exe)
#   • subkey with DisplayName matching 'BoltMate*'
# Collect their CustomActivator GUIDs as we go so we can also drop the
# matching HKCU\Software\Classes\CLSID\<GUID>\LocalServer32 entries.
$activatorGuids = @()
$aumidRoot = 'HKCU:\Software\Classes\AppUserModelId'
if (Test-Path $aumidRoot) {
    Get-ChildItem $aumidRoot | ForEach-Object {
        $name = $_.PSChildName
        $match = $false
        if ($name -ieq 'BoltMate') { $match = $true }
        if ($name -imatch 'boltmate') { $match = $true }
        try {
            $disp = (Get-ItemProperty -Path $_.PSPath -Name DisplayName -EA SilentlyContinue).DisplayName
            if ($disp -imatch '^BoltMate') { $match = $true }
        } catch {}
        if ($match) {
            try {
                $act = (Get-ItemProperty -Path $_.PSPath -Name CustomActivator -EA SilentlyContinue).CustomActivator
                if ($act) { $activatorGuids += $act }
            } catch {}
            Write-Host "    AppUserModelId\$name (activator: $act)"
            Remove-RegKey $_.PSPath
        }
    }
}

Write-Host '==> Scrubbing CustomActivator CLSIDs'
foreach ($guid in ($activatorGuids | Sort-Object -Unique)) {
    $clsidPath = "HKCU:\Software\Classes\CLSID\$guid"
    if (Test-Path $clsidPath) {
        Write-Host "    CLSID\$guid"
        Remove-RegKey $clsidPath
    }
}

Write-Host '==> Scrubbing Notifications\Settings entries'
# Same naming criteria as AppUserModelId scrub — including the auto-
# generated GUID entries WinAppSDK created. Remove anything matching
# BoltMate's name OR a CLSID we just dropped.
$notifRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
if (Test-Path $notifRoot) {
    Get-ChildItem $notifRoot | ForEach-Object {
        $name = $_.PSChildName
        $match = $false
        if ($name -ieq 'BoltMate') { $match = $true }
        if ($name -imatch 'boltmate') { $match = $true }
        foreach ($g in $activatorGuids) { if ($name -ieq $g) { $match = $true } }
        if ($match) {
            Write-Host "    Notifications\Settings\$name"
            Remove-RegKey $_.PSPath
        }
    }
}

Write-Host '==> Scrubbing WindowsAppSDK side files matching old CLSIDs'
$sdkAssets = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsAppSDK'
foreach ($guid in $activatorGuids) {
    Get-ChildItem $sdkAssets -Filter "$guid*" -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
}

Write-Host '==> Flushing shell icon cache + restarting explorer'
# Win taskbar / Start menu cache icons by exe path. A reinstall with a
# new embedded .ico bumps the file mod time but the shell keeps showing
# the cached icon until the cache dbs are deleted. Bounce Explorer so
# the next launch reads fresh icons from the rebuilt .exe.
Get-Process explorer -EA SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Remove-Item -Force "$env:LOCALAPPDATA\IconCache.db" -EA SilentlyContinue
Remove-Item -Force "$env:LOCALAPPDATA\Microsoft\Windows\Explorer\iconcache_*.db" -EA SilentlyContinue
Remove-Item -Force "$env:LOCALAPPDATA\Microsoft\Windows\Explorer\thumbcache_*.db" -EA SilentlyContinue
Start-Process explorer

Write-Host '==> Cleanup complete'
