#!/bin/bash
# Clothes toggle (F5 / clothes.request) verification, driven entirely through request files on
# install A. Hot reload only: it never restarts or kills the game.
#
#   ./clothes-verify.sh            run the sequence; captures are copied to shots/clothes-toggle/
#
# What it produces:
#   local-{clothed,unclothed,restored}.png     screen 640x360, same pose/teleport
#   local-{clothed,unclothed,restored}_mirror.png   the mirror's own RenderTexture (1536x1536)
#   diana-{clothed,unclothed}-{front,three}.png     frozen ortho rig, mannequin soloed
#   kiona-{clothed,unclothed}-{front,three}.png     frozen ortho rig, mannequin soloed
#   local-reload-clothed{,_mirror}.png          after an unclothed hot reload (must be clothed)
#
# Request files are written to a temp name and renamed: a direct write can be read while it is
# still open, which the module reports as "read failed" and then ignores.
set -u
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
LOG="$GAME/BepInEx/LogOutput.log"
S="$GAME/BepInEx/screens"
OUT="$(cd "$HOME/assets/monorepo/alterum-data/tracks/merged" && pwd)/shots/clothes-toggle"
T0=$(date +%s)

req()   { local f="$1" v="$2" t="$S/.$1.tmp"; printf '%s' "$v" > "$t" && mv -f "$t" "$S/$f"; }
say()   { echo; echo "=== $*"; }
pause() { sleep "${1:-2}"; }
mark()  { MARK=$(wc -l < "$LOG" 2>/dev/null || echo 0); }
since() { tail -n +$((MARK+1)) "$LOG" | grep -E "$1"; }

# Newest matching file written by THIS run (mtime after $T0); waits for the game to write it.
newest() {   # newest <dir> <glob>
    local dir="$1" pat="$2" f="" i
    for i in $(seq 1 24); do
        f=$(find "$dir" -maxdepth 1 -name "$pat" -newermt "@$T0" -printf '%T@ %p\n' 2>/dev/null |
            sort -nr | head -1 | cut -d' ' -f2-)
        [ -n "$f" ] && { printf '%s' "$f"; return 0; }
        sleep 0.5
    done
    return 1
}

grab() {   # grab <tag> <dest-name>
    local tag="$1" dest="$2" f
    f=$(newest "$S" "shot_main_${tag}_*.png") || { echo "  MISSING capture for '$tag'" >&2; return 1; }
    cp -f "$f" "$OUT/$dest"
    echo "  $dest <- $(basename "$f")"
}

grab_prop() {  # grab_prop <view> <tag> <dest-name>
    local view="$1" tag="$2" dest="$3" f
    f=$(newest "$S" "shot_main_prop_${view}_${tag}_*.png") || { echo "  MISSING capture for prop/$view/$tag" >&2; return 1; }
    cp -f "$f" "$OUT/$dest"
    echo "  $dest <- $(basename "$f")"
}

grab_mirror() {  # grab_mirror <tag> <dest-name>
    local tag="$1" dest="$2" f
    f=$(newest "$S/clothes" "${1}_mirror.png") || { echo "  MISSING mirror dump for '$tag'" >&2; return 1; }
    cp -f "$f" "$OUT/$dest"
    echo "  $dest <- $(basename "$f")"
}

mkdir -p "$OUT" "$S/clothes"

say "state: third person, player at the mirror"
mark; req pov.request on; pause 2; since "\[pov\] (request|third-person ON)"
req cam.request "teleport=to:1.58,-10.09"; pause 3
since "tp-cam:teleport" | tail -1

say "1. local player, clothed (screen + mirror)"
mark; req shot.request local-clothed; pause 2; since "\[shot\]" | tail -1
mark; req clothes.request "mirror=local-clothed"; pause 2; since "clothes\] mirror dump" | tail -1

say "2. local player, clothes off (screen + mirror), then the classification dump"
mark; req clothes.request $'off\nmirror=local-unclothed'; pause 2
since "clothes\] (off:|OFF|mirror dump)" | tail -3
mark; req shot.request local-unclothed; pause 2; since "\[shot\]" | tail -1
mark; req clothes.request "dump"; pause 2; since "clothes\] (classification|table|counts)" | tail -3

say "3. wardrobe mannequins (frozen ortho rig, same pose/camera for both states)"
# The local captures above end unclothed; the mannequin pair starts clothed.
req clothes.request "on"; pause 1
for M in "3P_ConcreteModel_Female_03_Diana:diana" "3P_ConcreteModel_Female_04_Kiona:kiona" "3P_ConcreteModel_Female_02:f02"; do
    model="${M%%:*}"; name="${M##*:}"
    # freeze first, then solo in a second request: freeze's own Release restores the solo set.
    mark; req propcap.request $'model='"$model"$'\nfreeze=1\ntag='"$name"$'-clothed\nviews=front,three\nwidth=720\nheight=1080'
    pause 7
    mark; req propcap.request $'model='"$model"$'\nsolo=1\ntag='"$name"$'-clothed\nviews=front,three\nwidth=720\nheight=1080'
    pause 6
    since "propcap:(freeze|solo)" | tail -2
    # unclothed: the model is already frozen + soloed, so this is the same camera and pose.
    req clothes.request "off"; pause 1
    mark; req propcap.request $'model='"$model"$'\nsolo=1\ntag='"$name"$'-unclothed\nviews=front,three\nwidth=720\nheight=1080'
    pause 6
    since "propcap:shot" | tail -2
    req clothes.request "on"; pause 1
    since "clothes\] (off:|on:|OFF|ON)" | tail -2
done
req propcap.request $'release=1'; pause 2; since "propcap:(release|solo\] restored)" | tail -2

say "4. restore: local player clothed again, same camera"
req clothes.request "on"; pause 1
mark; req shot.request local-restored; pause 2; since "\[shot\]" | tail -1
mark; req clothes.request "mirror=local-restored"; pause 2; since "clothes\] mirror dump" | tail -1
since "clothes\] on: restored" | tail -1

say "5. reversibility across a hot reload: off -> reload -> must start clothed"
req clothes.request "off"; pause 1
since "clothes\] off:" | tail -1
mark
cp -f "$(cd "$(dirname "$0")/.." && pwd)/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll" "$GAME/BepInEx/hot/.AlterumModelSwap.Hot.dll.tmp"
mv -f "$GAME/BepInEx/hot/.AlterumModelSwap.Hot.dll.tmp" "$GAME/BepInEx/hot/AlterumModelSwap.Hot.dll"
pause 10
since "\[hot\] generation|clothes\] (ready|set:|cleared stale)" | tail -4
req shot.request local-reload-clothed; pause 4; since "\[shot\]" | tail -1
req clothes.request "mirror=local-reload-clothed"; pause 4; since "clothes\] mirror dump" | tail -1

say "6. captures"
grab local-clothed   local-clothed.png
grab local-unclothed local-unclothed.png
grab local-restored  local-restored.png
grab local-reload-clothed local-reload-clothed.png
grab_mirror local-clothed   local-clothed_mirror.png
grab_mirror local-unclothed local-unclothed_mirror.png
grab_mirror local-restored  local-restored_mirror.png
grab_mirror local-reload-clothed local-reload-clothed_mirror.png
for name in diana kiona f02; do
    grab_prop front "$name-clothed"   "$name-clothed-front.png"
    grab_prop three "$name-clothed"   "$name-clothed-three.png"
    grab_prop front "$name-unclothed" "$name-unclothed-front.png"
    grab_prop three "$name-unclothed" "$name-unclothed-three.png"
done

say "7. log gates"
echo "  [Error] lines : $(grep -c '\[Error' "$LOG" || true)"
echo "  SWAP lines    : $(grep -c 'SWAP' "$LOG" || true)"
grep -E "mirror:body-check" "$LOG" | tail -2
grep -E "clothes\] (off:|on: restored)" "$LOG" | tail -6
echo
echo "done in $(( $(date +%s) - T0 ))s; captures in $OUT"
