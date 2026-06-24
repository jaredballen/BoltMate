#!/usr/bin/env bash
# release-win.sh — full Windows release pipeline driven from the Mac dev box.
#
# Steps:
#   1. Sync repo (src/ + .git + version.json) to VM at C:\dev\boltmate-build\repo\.
#   2. Kill running BoltMate + run unins000.exe + scrub appdata + reg keys
#      + firewall rules on the VM (skip with --no-clean).
#   3. SSH `dotnet publish` on the VM — self-contained WindowsAppSDK
#      requires Windows-only build tools (mt.exe) so cross-compile from
#      Mac is impossible; we run the publish on the host that can do it.
#   4. SSH ISCC.exe on the VM to compile the installer.
#   5. Rsync the produced installer back to <repo>/artifacts/win/.
#   6. Stage a copy at C:\dev\installers\ on the VM.
#   7. Run the installer silently (skip with --no-install) so BoltMate is
#      ready to launch immediately.
#
# Why this replaces the AfterTargets="Publish" MSBuild step:
#   dotnet publish for win-x64 used to work cross-platform from Mac. As
#   soon as we added Microsoft.WindowsAppSDK + WindowsAppSDKSelfContained=
#   true the build started requiring mt.exe (Windows manifest tool) which
#   is a Windows PE binary. Cross-compile fails with "cannot execute
#   binary file". Moving the entire publish step to the VM is the only
#   reliable path until either (a) Microsoft ships a portable manifest
#   tool or (b) we switch to framework-dependent WinAppSDK + manual
#   Bootstrap.TryInitialize() (which requires the SDK runtime pre-
#   installed on every target machine).
#
# Usage:
#   ./scripts/release-win.sh                   # defaults to Release config
#   CONFIG=Debug ./scripts/release-win.sh      # override config
#   ./scripts/release-win.sh --no-clean        # skip the wipe-vm-state step
#   ./scripts/release-win.sh --no-install      # build + stage, no install
#   ./scripts/release-win.sh --launch          # launch BoltMate after install

set -euo pipefail

CONFIG=${CONFIG:-Release}
SSH_HOST=${SSH_HOST:-boltmate-win}
# Repo root on the VM mirrors the local repo layout (including .git) so
# Nerdbank.GitVersioning can compute the version from history at publish
# time exactly the way it would on the Mac dev box.
VM_REPO=/c/dev/boltmate-build/repo
VM_SRC=$VM_REPO/src
VM_OUT=/c/dev/boltmate-build/out
LOCAL_REPO_ROOT=$(cd "$(dirname "$0")/.." && pwd)
LOCAL_SRC=$LOCAL_REPO_ROOT/src
LOCAL_ARTIFACTS=$LOCAL_REPO_ROOT/artifacts/win
ISCC='C:\Users\jallen\AppData\Local\Programs\Inno Setup 6\ISCC.exe'
TFM=net10.0-windows10.0.19041.0
RID=win-x64

NO_CLEAN=0
NO_INSTALL=0
LAUNCH=0
for arg in "$@"; do
    case "$arg" in
        --no-clean) NO_CLEAN=1 ;;
        --no-install) NO_INSTALL=1 ;;
        --launch) LAUNCH=1 ;;
    esac
done

echo "==> Ensuring staging dirs on $SSH_HOST"
ssh "$SSH_HOST" "powershell -Command \"New-Item -ItemType Directory -Force -Path 'C:\\dev\\boltmate-build\\repo','C:\\dev\\boltmate-build\\out' | Out-Null\""

echo "==> Syncing repo (src/ + .git + version.json) → $SSH_HOST:$VM_REPO"
# .git ships because Nerdbank.GitVersioning walks the working copy at
# publish time to compute height-from-version.json. version.json is at
# repo root so it has to land at $VM_REPO/, not $VM_SRC/. .git is small
# (~8M for this repo) so the transfer cost is negligible.
#
# Filter rules are first-match-wins. Excludes (bin/, obj/, artifacts/)
# need to appear before the broad includes that would otherwise pull them
# in. The trailing '- *' kicks out everything that no include matched —
# so doc/, .claude/, scripts/, etc. stay on the Mac side.
rsync -az --delete \
    --filter='- bin/' \
    --filter='- obj/' \
    --filter='- /artifacts/' \
    --filter='+ /version.json' \
    --filter='+ /.git/' \
    --filter='+ /.git/**' \
    --filter='+ /src/' \
    --filter='+ /src/BoltMate.*/' \
    --filter='+ /src/BoltMate.*/**' \
    --filter='+ /src/installer/' \
    --filter='+ /src/installer/**' \
    --filter='+ /src/Directory.Build.*' \
    --filter='+ /src/BoltMate.sln' \
    --filter='+ /src/nuget.config' \
    --filter='- *' \
    "$LOCAL_REPO_ROOT/" "$SSH_HOST:$VM_REPO/"

echo "==> Killing + uninstalling existing BoltMate on VM"
if [[ "$NO_CLEAN" = 0 ]]; then
    # Stage the cleanup ps1 to a known path on the VM and run it. Keeps
    # quote escaping out of the bash↔powershell-over-ssh chain — that
    # combo broke previously on $env:APPDATA expansion inside a quoted
    # remote command string (powershell parser saw the bash backslash-
    # escaped quote as an end-of-string).
    ssh "$SSH_HOST" 'powershell -Command "New-Item -ItemType Directory -Force -Path C:\dev\boltmate-build\scripts | Out-Null"'
    rsync -az "$LOCAL_REPO_ROOT/scripts/uninstall-boltmate-win.ps1" "$SSH_HOST:/c/dev/boltmate-build/scripts/"
    ssh "$SSH_HOST" 'powershell -ExecutionPolicy Bypass -File C:\dev\boltmate-build\scripts\uninstall-boltmate-win.ps1' || true
fi

echo "==> dotnet publish on VM (config=$CONFIG, rid=$RID, tfm=$TFM)"
ssh "$SSH_HOST" "powershell -Command \"cd C:\\dev\\boltmate-build\\repo\\src; dotnet publish BoltMate.App\\BoltMate.App.csproj -c $CONFIG -r $RID --self-contained -f $TFM\""

echo "==> Reading published version (NB.GV stamps it during publish)"
# ProductVersion from the binary metadata == NB.GV AssemblyInformationalVersion.
# That includes the +commitId suffix; strip for installer filename only.
VERSION=$(ssh "$SSH_HOST" "powershell -Command \"(Get-Item 'C:\\dev\\boltmate-build\\repo\\src\\BoltMate.App\\bin\\$CONFIG\\$TFM\\$RID\\publish\\BoltMate.exe').VersionInfo.ProductVersion\"" | tr -d '\r' | head -n1)
INSTALLER_VERSION=${VERSION%%+*}
echo "    version (full):      $VERSION"
echo "    version (installer): $INSTALLER_VERSION"

echo "==> Compiling installer on VM via ISCC"
ssh "$SSH_HOST" "powershell -Command \"& '$ISCC' '/DMyAppVersion=$INSTALLER_VERSION' '/DSourceDir=C:\\dev\\boltmate-build\\repo\\src\\BoltMate.App\\bin\\$CONFIG\\$TFM\\$RID\\publish' '/DOutputDir=C:\\dev\\boltmate-build\\out' 'C:\\dev\\boltmate-build\\repo\\src\\installer\\BoltMate.iss'\""

echo "==> Pulling installer back to $LOCAL_ARTIFACTS"
mkdir -p "$LOCAL_ARTIFACTS"
rsync -az --remove-source-files "$SSH_HOST:$VM_OUT/BoltMate-Setup-$INSTALLER_VERSION.exe" "$LOCAL_ARTIFACTS/"

echo "==> Staging installer on VM at C:\\dev\\installers\\"
ssh "$SSH_HOST" 'powershell -Command "New-Item -ItemType Directory -Force -Path C:\dev\installers | Out-Null"'
rsync -az "$LOCAL_ARTIFACTS/BoltMate-Setup-$INSTALLER_VERSION.exe" "$SSH_HOST:/c/dev/installers/"

if [[ "$NO_INSTALL" = 0 ]]; then
    echo "==> Installing on VM (silent, current-user)"
    # /VERYSILENT runs without UI; /SUPPRESSMSGBOXES kills any remaining
    # prompts; /CURRENTUSER matches the per-user install layout under
    # %LOCALAPPDATA%\Programs\BoltMate. /NORESTART because BoltMate
    # doesn't reboot the box. ExitCode 0/3010 are both success.
    ssh "$SSH_HOST" "powershell -Command \"Start-Process -FilePath C:\\dev\\installers\\BoltMate-Setup-$INSTALLER_VERSION.exe -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/CURRENTUSER','/NORESTART' -Wait\""
fi

if [[ "$LAUNCH" = 1 ]]; then
    echo "==> Launching BoltMate on VM"
    ssh "$SSH_HOST" 'powershell -Command "Start-Process -FilePath C:\Users\jallen\AppData\Local\Programs\BoltMate\BoltMate.exe"' || true
fi

echo ""
echo "==> Done."
echo "    Local artifact: $LOCAL_ARTIFACTS/BoltMate-Setup-$INSTALLER_VERSION.exe"
echo "    VM install:     C:\\dev\\installers\\BoltMate-Setup-$INSTALLER_VERSION.exe"
if [[ "$NO_INSTALL" = 0 ]]; then
    echo "    Installed at:   C:\\Users\\jallen\\AppData\\Local\\Programs\\BoltMate\\BoltMate.exe"
fi
