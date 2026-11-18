#!/bin/bash
# Capture one proportions state on install A, twice, keeping the second render.
#
#   tools/chestgrab.sh <outdir> <stem> <<'EOF'
#   breastscale=1.55
#   chestscale=1.22581
#   chestfix=1
#   views=three
#   EOF
#
# The request is sent twice with the same tag because a capture taken in the same request as a
# scale/weight change can render the PREVIOUS state (tracks/bones/shots/proportions-pass4/
# capture-lag-proof). Both sends use the same state, so the second is only a re-render.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DIR=${1:?outdir}
STEM=${2:?stem}
mkdir -p "$DIR"
body="$(cat)"
TAG=$(printf '%s' "$STEM" | tr -c 'A-Za-z0-9_-' '_')

for pass in 1 2; do
    printf 'tag=%s-p%s\n%s\n' "$STEM" "$pass" "$body" | "$HERE/propcap.sh" "$TAG-p$pass" "$STEM" "$DIR" >/dev/null 2>&1
    if [ "$pass" = 1 ]; then
        for f in "$DIR/$STEM"-*.png; do [ -e "$f" ] && mv -f "$f" "$f.pass1"; done
    fi
done
for f in "$DIR/$STEM"-*.png.pass1; do [ -e "$f" ] && rm -f "$f"; done
ls -1 "$DIR/$STEM"-*.png 2>/dev/null | sed 's/^/  /'
