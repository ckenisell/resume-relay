#!/usr/bin/env bash
# Builds the Linux edition and packs it with its installer into resume-relay-<version>-linux.tar.gz.
# Usage: packaging/linux/package.sh <output-folder>
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$(mkdir -p "${1:-$ROOT/artifacts}" && cd "${1:-$ROOT/artifacts}" && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
STAGE="$(mktemp -d)/resume-relay"

dotnet publish "$ROOT/src/ResumeRelay/ResumeRelay.csproj" -c Release -o "$STAGE/app" ${RESTORE_ARGS:-}
rm -f "$STAGE/app/web.config"
cp "$ROOT/packaging/linux/install.sh" "$ROOT/packaging/linux/uninstall.sh" "$STAGE/"
sed "s/@VERSION@/$VERSION/g" "$ROOT/packaging/linux/README.md" > "$STAGE/README.md"
chmod +x "$STAGE/install.sh" "$STAGE/uninstall.sh"
tar --owner=0 --group=0 -C "$(dirname "$STAGE")" -czf "$OUT/resume-relay-$VERSION-linux.tar.gz" resume-relay
echo "$OUT/resume-relay-$VERSION-linux.tar.gz"
