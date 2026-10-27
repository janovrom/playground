#!/usr/bin/env bash
# Usage: entrypoint.sh <mode> [args...]
#   test         <proj> [args]  native Linux `dotnet test`
#   build        <proj> [args]  native Linux `dotnet build`
#   publish-win  <proj> [args]  cross-publish win-x64 self-contained (native SDK) to ./artifacts/win-x64
#   wine-test    <proj> [args]  `dotnet test` with the Windows SDK under Wine (Windows-only tests run)
#   wine-build   <proj> [args]  `dotnet build` with the Windows SDK under Wine
#   wine-run     <exe> [args]   run a Windows executable under Wine
#   shell                       bash
set -euo pipefail

export WINEDEBUG="${WINEDEBUG:--all}" WINEARCH=win64 MSBUILDDISABLENODEREUSE=1
unset DISPLAY WAYLAND_DISPLAY || true

die() { echo "[dotnet-container] $*" >&2; exit 64; }
[[ $# -ge 1 ]] || die "usage: <test|build|publish-win|wine-test|wine-build|wine-run|shell> ..."
mode=$1; shift
cd /src

cleanup() { wineserver -k >/dev/null 2>&1 || true; }
trap cleanup EXIT

need_project() { [[ $# -ge 1 ]] || die "$mode needs a project/solution path"; [[ -e $1 ]] || die "not found: $1"; }

# Restore natively: NuGet signature validation fails inside Wine.
wine_dotnet() {
  local verb=$1 project=$2; shift 2
  dotnet restore "$project"
  local abs; abs=$(readlink -f "$project")
  wine64 'C:\dotnet\dotnet.exe' "$verb" "Z:${abs//\//\\}" \
    --no-restore -nodeReuse:false "$@"
}

case "$mode" in
  test|build)
    need_project "$@"; dotnet "$mode" "$@" ;;
  publish-win)
    need_project "$@"; project=$1; shift
    dotnet publish "$project" -c Release -r win-x64 --self-contained true -o artifacts/win-x64 "$@" ;;
  wine-test|wine-build)
    need_project "$@"; project=$1; shift
    wine_dotnet "${mode#wine-}" "$project" "$@" ;;
  wine-run)
    [[ $# -ge 1 ]] || die "wine-run needs an executable"
    wine64 "$@" ;;
  shell)
    exec bash ;;
  *) die "unknown mode '$mode'" ;;
esac
