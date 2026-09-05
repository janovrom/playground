#!/usr/bin/env bash
set -euo pipefail

readonly SERVICE_NAME="modular-monolith"
readonly SERVICE_USER="modularmonolith"
readonly SERVICE_GROUP="modularmonolith"
readonly INSTALL_DIR="/opt/modular-monolith"
readonly UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly PACKAGE_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this installer as root." >&2
    exit 1
fi

if ! getent group "${SERVICE_GROUP}" >/dev/null; then
    groupadd --system "${SERVICE_GROUP}"
fi

if ! getent passwd "${SERVICE_USER}" >/dev/null; then
    useradd \
        --system \
        --gid "${SERVICE_GROUP}" \
        --home-dir "${INSTALL_DIR}" \
        --shell /usr/sbin/nologin \
        --no-create-home \
        "${SERVICE_USER}"
fi

systemctl stop "${SERVICE_NAME}.service" 2>/dev/null || true

install --directory --owner "${SERVICE_USER}" --group "${SERVICE_GROUP}" --mode 0750 "${INSTALL_DIR}"
find "${INSTALL_DIR}" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -a "${PACKAGE_DIR}/." "${INSTALL_DIR}/"
rm -rf "${INSTALL_DIR}/scripts" "${INSTALL_DIR}/systemd"
chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${INSTALL_DIR}"

install \
    --owner root \
    --group root \
    --mode 0644 \
    "${PACKAGE_DIR}/systemd/${SERVICE_NAME}.service" \
    "${UNIT_PATH}"

systemctl daemon-reload
systemctl enable --now "${SERVICE_NAME}.service"
