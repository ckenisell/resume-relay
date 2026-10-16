#!/usr/bin/env bash
# Removes the Resume Relay service and program. Your players, names and log stay in
# /var/lib/resume-relay unless you add --purge.
set -euo pipefail
if [[ $EUID -ne 0 ]]; then exec sudo bash "$0" "$@"; fi

systemctl disable --now resume-relay 2>/dev/null || true
rm -f /etc/systemd/system/resume-relay.service
systemctl daemon-reload
rm -rf /opt/resume-relay

if [[ "${1:-}" == "--purge" ]]; then
  rm -rf /var/lib/resume-relay
  userdel resumerelay 2>/dev/null || true
  echo "Resume Relay and all its data were removed."
else
  echo "Resume Relay was removed. Settings kept in /var/lib/resume-relay (run with --purge to delete them)."
fi
