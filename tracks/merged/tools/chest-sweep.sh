#!/bin/bash
# proportions verification on the merged build (install A): chest-weight-fix sweep.
#
#   BODY='Optimized_Body' GARMENT='Undershirt' POUCH='Pouch' HIDEALL='Body,Legs_3,Belt,Boots,Pants,Pouch,Hair' \
#       tools/chest-sweep.sh <spawn-match> <prefix> <total>...
#
# For every chest total and both states of the garment-weight fix (on/off), through
# tools/chestgrab.sh (request sent twice, second render kept):
#   <prefix>-<total>-fx<0|1>         full
#   <prefix>-<total>-fx<0|1>-shirt   the garment alone (every other renderer hidden)
#   <prefix>-<total>-fx<0|1>-nop     the shoulder pouch hidden (it lies over the shirt by design)
# plus one garment-only reference at 1.55 (<prefix>-ref-shirt).
#
# The rig is spawned + frozen + soloed first (the local player's own body is hidden for the run and
# restored by release=1 at the end). Same method as tracks/bones/tools/chest-sweep.sh, retargeted
# at A's shared screens dir.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
TRACK="$(cd "$HERE/.." && pwd)"
DATA="$(cd "$HOME/assets/monorepo/alterum-data/tracks/merged" && pwd)"
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
SCREENS="$GAME/BepInEx/screens"
LOG="$GAME/BepInEx/LogOutput.log"
OUT=${OUT:-$DATA/shots/proportions-merged}
LEAF=${LEAF:-1.55}
BODY=${BODY:?set BODY=<skin mesh name substring>}
GARMENT=${GARMENT:?set GARMENT=<garment renderer name substring>}
POUCH=${POUCH:?set POUCH=<over-shirt accessory renderer name substring>}
HIDEALL=${HIDEALL:?set HIDEALL=<comma list of every renderer except the garment>}
MODEL=${1:?spawn match}
PREFIX=${2:?prefix}
shift 2
mkdir -p "$OUT"

req() { local f="$SCREENS/$1" t="$SCREENS/.$1.tmp"; printf '%s\n' "$2" > "$t" && mv -f "$t" "$f"; }

echo "== spawn $MODEL / freeze =="
req propcap.request "spawn=$MODEL
freeze=1
posecopy=1"
sleep 8
tail -n 30 "$LOG" | grep -aE '\[propcap:(spawn|freeze)|\[chest:(apply|skip)\]' | sed -e 's/^\[[^]]*\] //'

req propcap.request "solo=1"; sleep 2
# rest-pose captures: the jiggle solver offsets the leaves by up to ~10 mm, which is its own
# (frame-dependent) poke source; the proportions question is the rest shape.
req solver.request "enabled=0"; sleep 1

printf 'breastscale=%s
chestscale=1.0
chestfix=1
partsall=1
hideparts=%s
views=three' "$LEAF" "$HIDEALL" | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-ref-shirt" || true
cp -f "$SCREENS/propcap_${PREFIX}-ref-shirt-p2.txt" "$OUT/$PREFIX-ref-shirt-info.txt" 2>/dev/null || true

for total in "$@"; do
    cage=$(python3 -c "print(f'{$total/$LEAF:.5f}')")
    for fx in 1 0; do
        echo "== $PREFIX chest=$total cage=$cage chestfix=$fx =="
        printf 'breastscale=%s\nchestscale=%s\nchestfix=%s\npartsall=1\nviews=three' "$LEAF" "$cage" "$fx" \
            | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-$total-fx$fx" || true
        printf 'breastscale=%s\nchestscale=%s\nchestfix=%s\npartsall=1\nhideparts=%s\nviews=three' "$LEAF" "$cage" "$fx" "$HIDEALL" \
            | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-$total-fx$fx-shirt" || true
        printf 'breastscale=%s\nchestscale=%s\nchestfix=%s\npartsall=1\nhideparts=%s\nviews=three' "$LEAF" "$cage" "$fx" "$POUCH" \
            | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-$total-fx$fx-nop" || true
    done
done
req solver.request "enabled=1"
req propcap.request "release=1"
sleep 2
echo "== done: $(ls -1 "$OUT"/$PREFIX-*.png 2>/dev/null | wc -l) renders in $OUT =="
