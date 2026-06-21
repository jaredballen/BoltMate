#!/usr/bin/env bash
# Dump every TCC entry for BoltMate. macOS has no per-app permissions UI,
# so this is the only way to see what TCC remembers across services.
#
# Requires the calling terminal to have Full Disk Access:
#   System Settings → Privacy & Security → Full Disk Access → add Terminal/iTerm
#
# Usage: scripts/tcc-status.sh [bundle-id]
#   bundle-id defaults to com.jaredballen.BoltMate
set -u

BUNDLE_ID="${1:-com.jaredballen.BoltMate}"
USER_TCC="$HOME/Library/Application Support/com.apple.TCC/TCC.db"
SYS_TCC="/Library/Application Support/com.apple.TCC/TCC.db"

dump() {
    local db="$1"
    local scope="$2"
    if [ ! -r "$db" ]; then
        echo "[$scope] $db — not readable (grant Full Disk Access to this terminal)"
        return
    fi
    echo "[$scope] $db"
    sqlite3 -header -column "$db" \
        "SELECT service AS Service,
                CASE auth_value WHEN 0 THEN 'denied'
                                 WHEN 1 THEN 'unknown'
                                 WHEN 2 THEN 'allowed'
                                 WHEN 3 THEN 'limited'
                                 ELSE printf('val=%d', auth_value) END AS Auth,
                datetime(last_modified, 'unixepoch', 'localtime') AS Modified,
                client AS Client
         FROM access
         WHERE client = '${BUNDLE_ID}'
         ORDER BY service;"
    echo
}

dump "$USER_TCC" "USER"
dump "$SYS_TCC"  "SYSTEM"

cat <<EOF
Service legend (most common):
  kTCCServiceListenEvent         Input Monitoring (HID reads)
  kTCCServiceLocalNetwork        Local Network (LAN discovery / multicast)
  kTCCServiceAccessibility       Accessibility
  kTCCServiceSystemPolicyAllFiles Full Disk Access
  kTCCServiceScreenCapture       Screen Recording
EOF
