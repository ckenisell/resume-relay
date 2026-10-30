#!/usr/bin/env bash
# Resume Relay installer for Ubuntu.
# Installs (or upgrades) the service, keeps your players, names and settings on upgrade.
set -euo pipefail

APP_DIR=/opt/resume-relay
DATA_DIR=/var/lib/resume-relay
SERVICE=resume-relay
SVC_USER=resumerelay
PORT="${RESUMERELAY_PORT:-8090}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ $EUID -ne 0 ]]; then
  echo "This needs administrator rights; asking for your password…"
  exec sudo RESUMERELAY_PORT="$PORT" bash "$0" "$@"
fi

if [[ ! -f "$HERE/app/ResumeRelay.dll" ]]; then
  echo "Can't find app/ResumeRelay.dll next to this script. Run it from the unpacked resume-relay folder." >&2
  exit 1
fi

step() { printf '\n==> %s\n' "$1"; }

# 1. The .NET web runtime, version 8 or newer. Ubuntu's own archive has 10 on 26.04 and 8 on 22.04/24.04.
has_runtime() {
  command -v dotnet >/dev/null &&
    dotnet --list-runtimes 2>/dev/null | awk '$1=="Microsoft.AspNetCore.App"{split($2,v,"."); if (v[1]+0>=8) ok=1} END{exit !ok}'
}
step "Checking for the .NET runtime (8 or newer)"
if has_runtime; then
  echo "Already installed: $(dotnet --list-runtimes | awk '$1=="Microsoft.AspNetCore.App"{print $2}' | sort -V | tail -1)"
else
  apt-get update
  for pkg in aspnetcore-runtime-10.0 aspnetcore-runtime-8.0; do
    if apt-cache show "$pkg" >/dev/null 2>&1; then
      echo "Installing $pkg from Ubuntu's archive…"
      apt-get install -y "$pkg" && break
    fi
  done
  if ! has_runtime; then
    . /etc/os-release
    echo >&2
    echo "Ubuntu ${VERSION_ID:-?} (${VERSION_CODENAME:-?}) offers neither aspnetcore-runtime-10.0 nor aspnetcore-runtime-8.0." >&2
    echo "Install the ASP.NET Core runtime 8 or newer (https://learn.microsoft.com/dotnet/core/install/linux-ubuntu)," >&2
    echo "then run this script again." >&2
    exit 1
  fi
fi
DOTNET="$(command -v dotnet)"
command -v curl >/dev/null || apt-get install -y curl   # used below to confirm the service answers

# 1b. ffmpeg, which makes Live Poster's videos, and Intel's video driver so it can encode on the video hardware
# (Quick Sync, the same hardware Plex transcodes with). Optional: Resume Relay works without them.
step "Installing ffmpeg (for Live Poster)"
if command -v ffmpeg >/dev/null; then
  echo "Already installed."
else
  apt-get update -qq || true
  apt-get install -y ffmpeg || echo "Couldn't install ffmpeg; Live Poster will say so. Install it later with: sudo apt install ffmpeg"
fi
if [[ "$(dpkg --print-architecture)" == "amd64" ]] && grep -qi "GenuineIntel" /proc/cpuinfo; then
  dpkg -s intel-media-va-driver-non-free >/dev/null 2>&1 || dpkg -s intel-media-va-driver >/dev/null 2>&1 \
    || apt-get install -y intel-media-va-driver-non-free 2>/dev/null || apt-get install -y intel-media-va-driver 2>/dev/null || true
fi

# 2. A dedicated account with no login, so the service can only touch its own folder.
step "Creating the service account"
if id "$SVC_USER" >/dev/null 2>&1; then
  echo "Account '$SVC_USER' already exists."
else
  useradd --system --no-create-home --home-dir "$DATA_DIR" --shell /usr/sbin/nologin "$SVC_USER"
  echo "Created '$SVC_USER'."
fi
# The video hardware (/dev/dri) belongs to the render and video groups.
for g in render video; do getent group "$g" >/dev/null && usermod -aG "$g" "$SVC_USER"; done

# 3. Program files and data folder.
step "Copying the program to $APP_DIR"
systemctl stop "$SERVICE" 2>/dev/null || true
mkdir -p "$APP_DIR" "$DATA_DIR"
rm -rf "${APP_DIR:?}/"*
cp -r "$HERE/app/." "$APP_DIR/"
chown -R root:root "$APP_DIR"
chown -R "$SVC_USER:$SVC_USER" "$DATA_DIR"
chmod 750 "$DATA_DIR"

# 4. systemd service: starts at boot, restarts itself if it ever stops.
step "Setting up the background service"
cat > /etc/systemd/system/$SERVICE.service <<EOF
[Unit]
Description=Resume Relay (copies Zidoo bookmarks between players)
After=network-online.target
Wants=network-online.target

[Service]
User=$SVC_USER
Group=$SVC_USER
SupplementaryGroups=$(for g in render video; do getent group "$g" >/dev/null && printf '%s ' "$g"; done)
WorkingDirectory=$APP_DIR
ExecStart=$DOTNET $APP_DIR/ResumeRelay.dll
Environment=RESUMERELAY_DATA=$DATA_DIR
Environment=RESUMERELAY_PORT=$PORT
Environment=HOME=$DATA_DIR
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_NOLOGO=1
Restart=always
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$DATA_DIR
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now "$SERVICE"

# 5. Confirm it answers.
step "Checking that it started"
for _ in $(seq 1 20); do
  if curl -fsS "http://127.0.0.1:$PORT/api/state" >/dev/null 2>&1; then OK=1; break; fi
  sleep 1
done
if [[ "${OK:-0}" != 1 ]]; then
  echo "The service did not answer. Recent log lines:" >&2
  journalctl -u "$SERVICE" -n 30 --no-pager >&2 || true
  exit 1
fi

IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
echo
echo "Resume Relay is running."
echo "Open  http://${IP:-<this-computer>}:$PORT  in a browser on any device on your network."

if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q '^Status: active'; then
  if ! ufw status | grep -qE "^$PORT(/tcp)?\b"; then
    echo
    echo "Note: the Ubuntu firewall (ufw) is on and will block the page from other devices."
    echo "To allow it from your home network only, run:"
    echo "  sudo ufw allow from ${IP%.*}.0/24 to any port $PORT proto tcp"
  fi
fi
