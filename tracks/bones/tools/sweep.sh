#!/bin/bash
# Capture a chest-cage sweep on one frozen rig with the pass-3 proportions probe.
#
#   BODY=Diana_Optimized_Body GARMENT=Undershirt tools/sweep.sh <model> <tagprefix> <total>...
#
# "total" is the pass-2-style chest value (leaf 1.55 x cage), so 1.80 means cage=1.161.
# Per value: front/three/side full body, then front with the garment hidden (-nos) and front with
# the body skin hidden (-nob) - the two masks the poke-through analyser compares.
# Output: shots/proportions-pass3/raw/<tagprefix>-<total>-<view>.png and -info.txt
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
TRACK="$(cd "$HERE/.." && pwd)"
DATA="$(cd "$HOME/assets/monorepo/alterum-data/tracks/bones" && pwd)"
SCREENS=/mnt/c/AlterumT1/BepInEx/screens/t1
LOG=/mnt/c/AlterumT1/BepInEx/LogOutput.log
OUT="$DATA/shots/proportions-pass3/raw"
LEAF=${LEAF:-1.55}
BODY=${BODY:?set BODY=<skin mesh name substring>}
GARMENT=${GARMENT:?set GARMENT=<garment mesh name substring>}
EXTRA=${EXTRA:-}   # extra renderers to hide in the -nos pass (e.g. the shoulder pouch whose strap is legitimately in front)
mkdir -p "$OUT"

model=${1:?model}
prefix=${2:?tagprefix}
shift 2

send() { # tag, body
    local tag=$1 body=$2
    local start=$(wc -l < "$LOG")
    printf '%s\n' "$body" > "$SCREENS/propcap.request"
    local elapsed=0 quiet=0 last=$start
    while [ $elapsed -lt 300 ]; do
        sleep 0.5; elapsed=$((elapsed+1))
        local now=$(wc -l < "$LOG")
        if [ "$now" -ne "$last" ]; then last=$now; quiet=0; continue; fi
        quiet=$((quiet+1)); [ $quiet -ge 3 ] && break
    done
    tail -n +$((start+1)) "$LOG" | grep -E '\[propcap:(shot|done|parts|measure)|\[bounds:(scale|apply)' | sed -e 's/^\[[^]]*\] //'
}

collect() { # tag, outstem
    local tag=$1 stem=$2
    local info="$SCREENS/propcap_$tag.txt"
    [ -f "$info" ] || { echo "!! no info file for $tag" >&2; return 1; }
    for view in front three side; do
        local p=$(tr -d '\r' < "$info" | sed -n "s/^view=$view camera=.* path=//p" | head -1)
        [ -n "$p" ] || continue
        cp -f "$SCREENS/$p" "$OUT/$stem-$view.png"
    done
    tr -d '\r' < "$info" > "$OUT/$stem-info.txt"
    echo "  -> $stem"
}

if [ "${SPAWN:-1}" = "1" ]; then
    echo "== spawn $model / freeze =="
    printf 'spawn=%s\nfreeze=1\nposecopy=1\n' "$model" > "$SCREENS/propcap.request"
    sleep 6
    # isolate the clone: the local player's body is on the same layer and keeps animating
    printf 'solo=1\n' > "$SCREENS/propcap.request"
    sleep 2
    # rest-pose captures: no jiggle offset on the leaves (the whole point is the proportions)
    printf 'enabled=0\n' > "$SCREENS/solver.request"
    sleep 2
    tail -n 14 "$LOG" | grep -E '\[propcap:(spawn|freeze|solo)' | sed -e 's/^\[[^]]*\] //'
fi

for total in "$@"; do
    cage=$(python3 -c "print(f'{$total/$LEAF:.5f}')")
    stem="$prefix-$total"
    tag=$(printf '%s' "$stem" | tr -c 'A-Za-z0-9_-' '_')
    echo "== total=$total cage=$cage tag=$tag =="
    send "$tag" "partsall=1
tag=$tag
breastscale=$LEAF
chestscale=$cage
views=front,three,side"
    collect "$tag" "$stem"
    send "$tag-nos" "partsall=1
hideparts=$GARMENT,$EXTRA
tag=$tag-nos
breastscale=$LEAF
chestscale=$cage
views=front"
    collect "$tag-nos" "$stem-nos"
    send "$tag-nop" "partsall=1
hideparts=$EXTRA
tag=$tag-nop
breastscale=$LEAF
chestscale=$cage
views=front"
    collect "$tag-nop" "$stem-nop"
    send "$tag-nob" "partsall=1
hideparts=$BODY
tag=$tag-nob
breastscale=$LEAF
chestscale=$cage
views=front"
    collect "$tag-nob" "$stem-nob"
done
