#!/bin/bash
# Send one propcap.request to T1 and collect the rendered views.
#
#   tools/grab.sh <tag> <outdir/stem> <<'EOF'
#   partsall=1
#   tag=<tag>
#   breastscale=1.55
#   chestscale=1.161
#   views=three
#   EOF
#
# Writes <outdir>/<stem>-<view>.png and <outdir>/<stem>-info.txt.
# Used by the strap attribution (same frozen rig for every hide/show combination).
set -uo pipefail
SCREENS=/mnt/c/AlterumT1/BepInEx/screens/t1
LOG=/mnt/c/AlterumT1/BepInEx/LogOutput.log
tag=${1:?tag}
stem=${2:?stem}
outdir=${3:-$(cd "$HOME/assets/monorepo/alterum-data/tracks/bones" && pwd)/shots/proportions-pass3/raw}
mkdir -p "$outdir"

body="$(cat)"
start=$(wc -l < "$LOG")
printf '%s\n' "$body" > "$SCREENS/propcap.request"

last=$start; quiet=0; elapsed=0
while [ "$elapsed" -lt 600 ]; do
    sleep 0.5; elapsed=$((elapsed + 1))
    now=$(wc -l < "$LOG")
    if [ "$now" -ne "$last" ]; then last=$now; quiet=0; continue; fi
    quiet=$((quiet + 1)); [ "$quiet" -ge 4 ] && break
done
tail -n +$((start + 1)) "$LOG" | grep -E '\[propcap:(shot|done|parts|freeze|camera)|\[bounds:(scale|apply)' | sed -e 's/^\[[^]]*\] //'

info="$SCREENS/propcap_$tag.txt"
[ -f "$info" ] || { echo "!! no info file for $tag" >&2; exit 1; }
for view in front three side; do
    p=$(tr -d '\r' < "$info" | sed -n "s/^view=$view camera=.* path=//p" | head -1)
    [ -n "$p" ] || continue
    cp -f "$SCREENS/$p" "$outdir/$stem-$view.png"
done
tr -d '\r' < "$info" > "$outdir/$stem-info.txt"
echo "  -> $outdir/$stem"
