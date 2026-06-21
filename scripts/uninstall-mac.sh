#!/usr/bin/env bash
# Full BoltMate uninstall for development testing of the first-run flow.
#
# What this kills:
#   • running App + CLI processes
#   • installed LaunchAgents (App + CLI)
#   • on-disk settings, logs, caches, the lock file
#   • TCC entries that tccutil can reset (ListenEvent, LocalNetwork,
#     Accessibility, SystemPolicyAllFiles) — LocalNetwork usually errors
#     on Sequoia; toggle off manually in System Settings → Privacy &
#     Security → Local Network when prompted at the end.
#   • optionally the .app bundle from /Applications and the publish output
#
# Usage:
#   scripts/uninstall-mac.sh           # quit + remove state, leaves dev .app intact
#   scripts/uninstall-mac.sh --hard    # also removes /Applications/BoltMate.app
#                                        and src/BoltMate.App/bin publish output
set -u

BUNDLE_ID="com.jaredballen.BoltMate"
HARD=0
for arg in "$@"; do
    case "$arg" in
        --hard) HARD=1 ;;
    esac
done

say() { echo "→ $*"; }

say "Killing running processes"
pkill -f BoltMate.App 2>/dev/null || true
pkill -f BoltMate.Cli 2>/dev/null || true
pkill -f boltmate 2>/dev/null || true
sleep 1

say "Unloading LaunchAgents"
for plist in ~/Library/LaunchAgents/com.jaredballen.boltmate*.plist; do
    [ -f "$plist" ] || continue
    launchctl unload "$plist" 2>/dev/null || true
done
rm -f ~/Library/LaunchAgents/com.jaredballen.boltmate*.plist

say "Removing on-disk state"
rm -rf ~/Library/Application\ Support/BoltMate
rm -rf ~/Library/Logs/BoltMate
rm -rf ~/Library/Caches/${BUNDLE_ID}
rm -rf ~/Library/Saved\ Application\ State/${BUNDLE_ID}.savedState

say "Resetting TCC entries (LocalNetwork commonly errors — toggle in Settings manually)"
for service in ListenEvent LocalNetwork Accessibility SystemPolicyAllFiles; do
    tccutil reset "$service" "$BUNDLE_ID" 2>&1 | sed 's/^/    /'
done

if [ "$HARD" -eq 1 ]; then
    say "Hard mode: removing /Applications/BoltMate.app"
    rm -rf /Applications/BoltMate.app
    say "Hard mode: removing publish output"
    rm -rf "$(dirname "$0")/../src/BoltMate.App/bin"
    rm -rf "$(dirname "$0")/../src/BoltMate.App/obj"
fi

cat <<EOF

Done. macOS Sequoia note: tccutil cannot reliably reset LocalNetwork —
if BoltMate still appears in System Settings → Privacy & Security →
Local Network, toggle it OFF manually before relaunching to see a fresh
prompt.
EOF
