#!/usr/bin/env bash
set -euo pipefail

readonly SERVICE_NAME="modular-monolith.service"
readonly UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}"

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this service controller as root." >&2
    exit 1
fi

if [[ ! -f "${UNIT_PATH}" ]]; then
    echo "${SERVICE_NAME} is not installed. Run install.sh first." >&2
    exit 1
fi

case "${1:-}" in
    start)
        systemctl start "${SERVICE_NAME}"
        systemctl status "${SERVICE_NAME}" --no-pager
        ;;
    stop)
        systemctl stop "${SERVICE_NAME}"
        systemctl status "${SERVICE_NAME}" --no-pager
        ;;
    *)
        echo "Usage: $0 {start|stop}" >&2
        exit 2
        ;;
esac
