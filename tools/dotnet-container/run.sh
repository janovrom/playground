#!/usr/bin/env bash
# Host wrapper: builds the image on first use and runs a mode against the repo root.
# Usage: tools/dotnet-container/run.sh <mode> [args...]
#   e.g. tools/dotnet-container/run.sh wine-test modular-monolith/ModularMonolith.slnx -c Release
# Env: IMAGE (default dotnet-wine), DOTNET_SDK_VERSION, CONTAINER_ENGINE (default podman), REBUILD=1
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
repo=$(cd "$here/../.." && pwd)
engine=${CONTAINER_ENGINE:-podman}
image=${IMAGE:-dotnet-wine}
sdk=${DOTNET_SDK_VERSION:-10.0.112}

if [[ -n ${REBUILD:-} ]] || ! $engine image exists "$image" 2>/dev/null; then
  $engine build --build-arg "DOTNET_SDK_VERSION=$sdk" -t "$image" "$here"
fi

# Persistent NuGet cache shared between runs.
tty=(); [[ -t 0 && -t 1 ]] && tty=(-it)

exec $engine run --rm "${tty[@]}" \
  -v "$repo:/src:Z" \
  -v dotnet-wine-nuget:/root/.nuget/packages \
  "$image" "$@"
