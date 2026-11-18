#!/bin/bash
# Capture one proportions-pass5 state, twice, keeping the second render.
#
#   tools/chestgrab.sh <outdir> <stem> <<'EOF'
#   breastscale=1.55
#   chestscale=1.097
#   chestfix=1
#   views=three
#   EOF
#
# The tag is derived from <stem>; the request is sent twice with the same tag because a capture
# taken in the same request as a scale/weight change renders the PREVIOUS state (see
# shots/proportions-pass4/capture-lag-proof/). Both sends use the same body, so the second send
# changes nothing - it only re-renders, which is the point.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DIR=${1:?outdir}
STEM=${2:?stem}
mkdir -p "$DIR"
body="$(cat)"
# the game sanitises the tag to [A-Za-z0-9_-] when naming propcap_<tag>.txt; match that
TAG=$(printf '%s' "$STEM" | tr -c 'A-Za-z0-9_-' '_')

for pass in 1 2; do
    printf 'tag=%s-p%s\n%s\n' "$STEM" "$pass" "$body" | "$HERE/grab.sh" "$TAG-p$pass" "$STEM" "$DIR" >/dev/null 2>&1
    # grab.sh writes <stem>-<view>.png; keep only the second pass's files
    if [ "$pass" = 1 ]; then
        for f in "$DIR/$STEM"-*.png; do [ -e "$f" ] && mv -f "$f" "$f.pass1"; done
    fi
done
for f in "$DIR/$STEM"-*.png.pass1; do [ -e "$f" ] && rm -f "$f"; done
ls -1 "$DIR/$STEM"-*.png 2>/dev/null | sed 's/^/  /'
