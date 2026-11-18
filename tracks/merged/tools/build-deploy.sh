#!/bin/bash
# Build the merged hot assembly, FAIL on a compile error, then deploy (same target rules as
# tools/deploy-hot.sh). deploy-hot.sh swallows `error CS` and copies whatever DLL is already in
# bin/, which silently deploys a stale build; this wrapper exists so that cannot happen.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MOD="$HERE/../mod"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en

out=$(cd "$MOD/AlterumModelSwap.Hot" && dotnet build -c Release -v q --nologo 2>&1)
rc=$?
if [ $rc -ne 0 ] || grep -qE "error [A-Z]+[0-9]+" <<<"$out"; then
    echo "$out" | grep -E "error [A-Z]+[0-9]+" | sort -u
    echo "BUILD FAILED - nothing deployed" >&2
    exit 1
fi
exec "$HERE/deploy-hot.sh"
