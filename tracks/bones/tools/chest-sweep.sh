#!/bin/bash
# proportions pass 5: chest-weight-fix sweep.
#
#   BODY='Optimized_Body' GARMENT='Undershirt' POUCH='Pouch' HIDEALL='Body,Legs_3,Belt,...' \
#       tools/chest-sweep.sh <model> <prefix> <total>...
#
# For every chest total and both states of the garment-weight fix (on/off) it captures, through
# tools/chestgrab.sh (request sent twice, second render kept):
#   <prefix>-<total>-fx-<view>     full
#   <prefix>-<total>-fx-shirt      the garment alone (every other renderer hidden) - its own
#                                  silhouette, which is where the pixel test counts
#   <prefix>-<total>-fx-nop        the shoulder pouch hidden (it lies over the shirt by design)
# plus one garment-only reference at 1.55 (<prefix>-ref-shirt).
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
TRACK="$(cd "$HERE/.." && pwd)"
DATA="$(cd "$HOME/assets/monorepo/alterum-data/tracks/bones" && pwd)"
SCREENS=/mnt/c/AlterumT1/BepInEx/screens/t1
LOG=/mnt/c/AlterumT1/BepInEx/LogOutput.log
OUT="$DATA/shots/proportions-pass5"
LEAF=${LEAF:-1.55}
BODY=${BODY:?set BODY=<skin mesh name substring>}
GARMENT=${GARMENT:?set GARMENT=<garment renderer name substring>}
POUCH=${POUCH:?set POUCH=<over-shirt accessory renderer name substring>}
HIDEALL=${HIDEALL:?set HIDEALL=<comma list of every renderer except the garment>}
MODEL=${1:?model}
PREFIX=${2:?prefix}
shift 2
mkdir -p "$OUT"

echo "== spawn $MODEL / freeze =="
printf 'spawn=%s\nfreeze=1\n' "$MODEL" > "$SCREENS/propcap.request"
sleep 7
tail -n 40 "$LOG" | grep -E '\[propcap:(spawn|freeze)|\[chest:(apply|skip)\]' | sed -e 's/^\[[^]]*\] //'

# rest-pose captures: the jiggle solver offsets the leaves by up to ~10 mm, which is its own
# (frame-dependent) poke source; the proportions question is the rest shape.
printf 'enabled=0\n' > "$SCREENS/solver.request"
sleep 2

# garment alone at the known-clean reference value: the footprint the pixel test counts in
printf 'tag=%s-ref\nbreastscale=%s\nchestscale=1.0\nchestfix=1\npartsall=1\nhideparts=%s\nviews=three\n' \
    "$PREFIX" "$LEAF" "$HIDEALL" | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-ref-shirt" || true
cp -f "$SCREENS/propcap_${PREFIX}-ref-p2.txt" "$OUT/$PREFIX-ref-shirt-info.txt" 2>/dev/null || true

for total in "$@"; do
    cage=$(python3 -c "print(f'{$total/$LEAF:.5f}')")
    for fx in 1 0; do
        echo "== $PREFIX chest=$total cage=$cage chestfix=$fx =="
        printf 'breastscale=%s\nchestscale=%s\nchestfix=%s\npartsall=1\nviews=three\n' "$LEAF" "$cage" "$fx" \
            | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-$total-fx$fx" || true
        printf 'breastscale=%s\nchestscale=%s\nchestfix=%s\npartsall=1\nhideparts=%s\nviews=three\n' "$LEAF" "$cage" "$fx" "$HIDEALL" \
            | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-$total-fx$fx-shirt" || true
        printf 'breastscale=%s\nchestscale=%s\nchestfix=%s\npartsall=1\nhideparts=%s\nviews=three\n' "$LEAF" "$cage" "$fx" "$POUCH" \
            | "$HERE/chestgrab.sh" "$OUT" "$PREFIX-$total-fx$fx-nop" || true
    done
done
printf 'enabled=1\n' > "$SCREENS/solver.request"
echo "== done: $(ls -1 "$OUT"/$PREFIX-*.png | wc -l) renders in $OUT =="
