#!/usr/bin/env bash
set -euo pipefail

readonly SERVICE_NAME="modular-monolith"
readonly SERVICE_USER="modularmonolith"
readonly SERVICE_GROUP="modularmonolith"
readonly INSTALL_DIR="/opt/modular-monolith"
readonly UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this uninstaller as root." >&2
    exit 1
fi

systemctl disable --now "${SERVICE_NAME}.service" 2>/dev/null || true
rm -f "${UNIT_PATH}"
systemctl daemon-reload
rm -rf "${INSTALL_DIR}"

if getent passwd "${SERVICE_USER}" >/dev/null; then
    userdel "${SERVICE_USER}"
fi

if group_entry="$(getent group "${SERVICE_GROUP}")"; then
    group_id="${group_entry#*:*:}"
    group_id="${group_id%%:*}"

    if ! getent passwd | awk -F: -v group_id="${group_id}" '$4 == group_id { found = 1 } END { exit !found }'; then
        groupdel "${SERVICE_GROUP}"
    fi
fi
