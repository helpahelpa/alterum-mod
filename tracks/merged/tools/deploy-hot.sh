#!/bin/bash
# Build the merged Hot assembly and hot-deploy it to the main install (A) and to C:\AlterumB.
#
#   tools/deploy-hot.sh          build + deploy both
#   tools/deploy-hot.sh build    build only
#
# A is running the merged build under hot reload; this only replaces BepInEx/hot/AlterumModelSwap.Hot.dll
# (atomic mv, never a write through a hardlink) and never restarts or stops anything.
# B's hot dir is BepInEx/hot/b/, matching the instance name B launches with.
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
MOD="$HERE/../mod"
DLL="$MOD/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
A="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"   # the pinned install; see ../pinned/UPDATE-POLICY.md
B='/mnt/c/AlterumB'
A_HOT="$A/BepInEx/hot"
B_HOT="$B/BepInEx/hot/b"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en

build() {
    for p in AlterumModelSwap.Contracts AlterumModelSwap AlterumModelSwap.Hot; do
        (cd "$MOD/$p" && dotnet build -c Release -v q --nologo 2>&1 | grep -E "error CS" ) || true
    done
    [ -f "$DLL" ] || { echo "build produced no dll" >&2; exit 1; }
}

deploy_one() {
    local dir="$1"
    mkdir -p "$dir" || exit 1
    cp "$DLL" "$dir/.AlterumModelSwap.Hot.dll.tmp" || exit 1
    mv -f "$dir/.AlterumModelSwap.Hot.dll.tmp" "$dir/AlterumModelSwap.Hot.dll" || exit 1
    echo "  -> $dir/AlterumModelSwap.Hot.dll ($(md5sum "$dir/AlterumModelSwap.Hot.dll" | cut -d' ' -f1))"
}

case "${1:-all}" in
    build) build; echo "built $(md5sum "$DLL" | cut -d' ' -f1)"; exit 0 ;;
esac

build
echo "deploying $(md5sum "$DLL" | cut -d' ' -f1)"
deploy_one "$A_HOT"
deploy_one "$B_HOT"
