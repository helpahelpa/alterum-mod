#!/bin/bash
# One round-trip on instance A: leave a spawn.request in place, then (re)deploy our hot dll until
# the request is consumed, and print the lines it produced.
#
# Instance A is shared with another agent's harness (`~/assets/monorepo/alterum-data/.temp/hatanchor/env.sh`) that
# hot-deploys to the same BepInEx/hot every minute or two. The request file is inert for a build
# without our probe, so parking it first and re-deploying until it disappears survives that race;
# ordering it the other way round does not (our dll gets replaced before the next poll).
#
#   tools/probe.sh "probe" [timeout_s] [grep-pattern] [max-deploys]
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
A="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
SCR="$A/BepInEx/screens"
LOG="$A/BepInEx/LogOutput.log"
HOT="$A/BepInEx/hot"
DLL="$HERE/../mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
BODY=${1:-probe}
TIMEOUT=${2:-90}
PAT=${3:-'\[spawn\|\[reel:pool'}
MAXDEPLOY=${4:-8}

deploy() {
    cp "$DLL" "$HOT/.AlterumModelSwap.Hot.dll.tmp" && mv -f "$HOT/.AlterumModelSwap.Hot.dll.tmp" "$HOT/AlterumModelSwap.Hot.dll"
}

rm -f "$SCR/spawn.request"
mv=$(wc -l < "$LOG")
printf '%s\n' "$BODY" > "$SCR/spawn.request"
for d in $(seq 1 "$MAXDEPLOY"); do
    deploy || exit 1
    for i in $(seq 1 12); do
        sleep 0.5
        [ -e "$SCR/spawn.request" ] || break 2
    done
done
for i in $(seq 1 "$TIMEOUT"); do
    [ -e "$SCR/spawn.request" ] && { sleep 1; continue; }
    break
done
if [ -e "$SCR/spawn.request" ]; then
    echo "request never consumed after $MAXDEPLOY deploys" >&2
    exit 2
fi
sleep 1
tail -n +$((mv + 1)) "$LOG" | grep -a "$PAT" | cut -c1-2000
