#!/bin/bash
# Send one propcap.request to the running main install (A) and collect the rendered views.
# Same contract as tracks/bones/tools/grab.sh, but for A's shared screens dir instead of T1.
#
#   tools/propcap.sh <tag> <stem> <outdir> <<'EOF'
#   spawn=Female_3_Diana
#   freeze=1
#   ...
#   EOF
#
# The request is written to a temp file and renamed: the module polls the file in Update and
# deletes it as a one-shot, so a partial read must never be possible.
# Writes <outdir>/<stem>-<view>.png and <outdir>/<stem>-info.txt.
set -uo pipefail
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
SCREENS="$GAME/BepInEx/screens"
LOG="$GAME/BepInEx/LogOutput.log"
tag=${1:?tag}
stem=${2:?stem}
outdir=${3:?outdir}
mkdir -p "$outdir"
body="$(cat)"

req() { # file, body
    local f="$SCREENS/$1" t="$SCREENS/.$1.tmp"
    printf '%s\n' "$2" > "$t" && mv -f "$t" "$f"
}

start=$(wc -l < "$LOG")
req propcap.request "tag=$tag
$body"

# wait for the module's own completion line and then for the log to go quiet
done_re="\[propcap:(done|finish)\] tag='$tag'"
elapsed=0; quiet=0; last=$start
while [ "$elapsed" -lt 900 ]; do
    sleep 0.5; elapsed=$((elapsed + 1))
    if tail -n +$((start + 1)) "$LOG" | grep -qE "$done_re"; then sleep 0.2; break; fi
    now=$(wc -l < "$LOG")
    if [ "$now" -ne "$last" ]; then last=$now; quiet=0; continue; fi
    quiet=$((quiet + 1)); [ "$quiet" -ge 6 ] && break
done
tail -n +$((start + 1)) "$LOG" | grep -aE '\[propcap:(shot|done|parts|freeze|solo|spawn|measure|camera)|\[bounds:(scale|apply)|\[chest:(apply|skip|restore)' | sed -e 's/^\[[^]]*\] //' | tail -40

info="$SCREENS/propcap_$tag.txt"
if [ ! -f "$info" ]; then echo "!! no info file for $tag" >&2; exit 1; fi
for view in front three side back; do
    p=$(tr -d '\r' < "$info" | sed -n "s/^view=$view camera=.* path=//p" | head -1)
    [ -n "$p" ] || continue
    cp -f "$SCREENS/$p" "$outdir/$stem-$view.png"
done
tr -d '\r' < "$info" > "$outdir/$stem-info.txt"
echo "  -> $outdir/$stem"
