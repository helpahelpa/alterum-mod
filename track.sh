#!/bin/bash
# Drive one feature track in complete isolation from the other tracks and from A/B.
#
#   ./track.sh <bones|emotes|cosmetics> build   # build that track's hot assembly only
#   ./track.sh <bones|emotes|cosmetics> deploy  # copy its DLL into that track's install hot dir
#   ./track.sh <bones|emotes|cosmetics> run     # launch that track's install (headed, 640x360, tiled)
#   ./track.sh <bones|emotes|cosmetics> log     # tail that track's LogOutput.log
#   ./track.sh <bones|emotes|cosmetics> stop    # stop ONLY that track's install (match on exe path)
#   ./track.sh <bones|emotes|cosmetics> shot    # desktop screenshot via win/run-helper.ps1
#
# extras: status (what is running), cfg (print the per-instance cfg path), build-all (host+contracts+hot)
#
# Isolation rules this script enforces:
#   * every command works on tracks/<track>/mod/ and C:\AlterumT<t> only
#   * stop matches the install's own Alterum.exe path, never a process name
#   * run refuses to start a second copy of the same install
#   * deploys replace the hot dll atomically (mv), so a hardlink is never written through
set -uo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
WIN='/mnt/c/Windows/Temp/alterum'
WINT='C:\Windows\Temp\alterum'

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
case ":$PATH:" in *":$HOME/.dotnet:"*) ;; *) PATH="$HOME/.dotnet:$PATH" ;; esac
export PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_UI_LANGUAGE=en

usage() {
    sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-1}"
}

TRACK="${1:-}"
CMD="${2:-}"
[ -z "$TRACK" ] && usage 1
[ "$TRACK" = "-h" ] || [ "$TRACK" = "--help" ] && usage 0

case "$TRACK" in
    bones)     INST=t1; WINSTALL='C:\AlterumT1'; INSTALL=/mnt/c/AlterumT1; SLOT=0 ;;
    emotes)    INST=t2; WINSTALL='C:\AlterumT2'; INSTALL=/mnt/c/AlterumT2; SLOT=1 ;;
    cosmetics) INST=t3; WINSTALL='C:\AlterumT3'; INSTALL=/mnt/c/AlterumT3; SLOT=2 ;;
    *) echo "unknown track '$TRACK' (bones|emotes|cosmetics)" >&2; usage 1 ;;
esac

TRACKDIR="$HERE/tracks/$TRACK"
MOD="$TRACKDIR/mod"
HOT_DLL="$MOD/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
REMOTE_HOT="$INSTALL/BepInEx/hot/$INST"
LOG="$INSTALL/BepInEx/LogOutput.log"
CFG="$INSTALL/BepInEx/config/com.user0.alterum.modelswap.$INST.cfg"
BASE_CFG="$INSTALL/BepInEx/config/com.user0.alterum.modelswap.cfg"

die() { echo "error: $*" >&2; exit 1; }

check_install() {
    local marker="$INSTALL/TRACK-ISOLATION.txt"
    [ -d "$INSTALL" ] || die "install $INSTALL does not exist (see tracks/README.md)"
    [ -f "$marker" ] || die "$INSTALL has no TRACK-ISOLATION.txt - refusing to touch it"
    grep -qx "track=$TRACK" "$marker" || die "$INSTALL belongs to '$(sed -n 's/^track=//p' "$marker")', not '$TRACK'"
}

# Run a helper .ps1 from C:\Windows\Temp\alterum (session 0 -> schedules interactive work itself).
remote_ps() {
    local name="$1"; shift
    ssh windows powershell -NoProfile -ExecutionPolicy Bypass -File "C:\\Windows\\Temp\\alterum\\$name" "$@" 2>&1 |
        grep -vE 'post-quantum|store now|openssh\.com|vulnerable to|WARNING: connection|^\s*$' || true
}

sync_win() {
    mkdir -p "$WIN"
    cp -f "$HERE"/win/*.ps1 "$WIN"/ 2>/dev/null || true
}

# Interactive desktop size (the SSH session sees a fake 1024x768). Prints "W H" of the usable area.
desktop_size() {
    local json="$WIN/desktop-size.json" w="" h=""
    sync_win
    rm -f "$json"
    remote_ps run-helper.ps1 -Script "$WINT\\desktop-size.ps1" >/dev/null 2>&1
    for _ in $(seq 1 30); do [ -f "$json" ] && break; sleep 0.5; done
    if [ -f "$json" ]; then
        read -r w h < <(python3 - "$json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1], encoding='utf-8-sig'))
print(d.get('workWidth') or d['width'], d.get('workHeight') or d['height'])
PY
        ) || true
    fi
    if [[ ! "$w" =~ ^[0-9]+$ || ! "$h" =~ ^[0-9]+$ || "${w:-0}" -lt 640 ]]; then
        echo "warning: could not read the interactive desktop size; assuming ${TRACK_DESKTOP:-1920x1080}" >&2
        echo "${TRACK_DESKTOP:-1920x1080}" | tr 'xX' '  '
    else
        echo "$w $h"
    fi
}

# Create the install's own cfg from the base one (first use) and pin the window keys.
ensure_cfg() {
    [ -f "$BASE_CFG" ] || die "base cfg missing: $BASE_CFG"
    if [ ! -f "$CFG" ]; then
        cp "$BASE_CFG" "$CFG"
        python3 - "$CFG" <<'PY'
import re, sys
p = sys.argv[1]
s = open(p, encoding='utf-8-sig').read()
s = re.sub(r'(?m)^ClickSequence = .*$', 'ClickSequence = ', s)   # never inherit A's lobby automation
s = re.sub(r'(?m)^Screenshots = .*$', 'Screenshots = false', s)  # opt in per track, do not spam the dir
open(p, 'w', encoding='utf-8').write(s)
print("created", p)
PY
    fi
}

set_window_cfg() { # x y w h
    python3 - "$CFG" "$1" "$2" "$3" "$4" <<'PY'
import re, sys
p, x, y, w, h = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5]
s = open(p, encoding='utf-8-sig').read()
def setk(k, v):
    global s
    if re.search(r'(?m)^' + re.escape(k) + r' = ', s):
        s = re.sub(r'(?m)^' + re.escape(k) + r' = .*$', k + ' = ' + v, s)
    else:
        s = s.replace('[Window]', '[Window]\n' + k + ' = ' + v, 1)
setk('ForceWindowed', 'true')
setk('Quiet', 'false')
setk('WindowX', x); setk('WindowY', y)
setk('QuietWidth', w); setk('QuietHeight', h)
open(p, 'w', encoding='utf-8').write(s)
PY
}

case "$CMD" in
build)
    [ -d "$MOD/AlterumModelSwap.Hot" ] || die "source copy missing: $MOD"
    ( cd "$MOD/AlterumModelSwap.Hot" && dotnet build -c Release -v q --nologo ) || exit $?
    [ -f "$HOT_DLL" ] || die "build produced no $HOT_DLL"
    echo "built $HOT_DLL"
    ;;

build-all)
    for p in AlterumModelSwap.Contracts AlterumModelSwap AlterumModelSwap.Hot; do
        ( cd "$MOD/$p" && dotnet build -c Release -v q --nologo ) || exit $?
    done
    echo "built host + contracts + hot in $MOD"
    ;;

deploy)
    check_install
    [ -f "$HOT_DLL" ] || die "no build yet - run: ./track.sh $TRACK build"
    mkdir -p "$REMOTE_HOT"
    cp "$HOT_DLL" "$REMOTE_HOT/.hot.dll.tmp" && mv -f "$REMOTE_HOT/.hot.dll.tmp" "$REMOTE_HOT/AlterumModelSwap.Hot.dll"
    echo "deployed -> $WINSTALL\\BepInEx\\hot\\$INST\\AlterumModelSwap.Hot.dll"
    if remote_ps track-instance.ps1 -Action status -Install "$WINSTALL" -Instance "$INST" | grep -q '^running'; then
        echo "game is running: the host reloads this dll within ~1s (watch: ./track.sh $TRACK log)"
    else
        echo "game is not running: ./track.sh $TRACK run"
    fi
    ;;

run)
    check_install
    if remote_ps track-instance.ps1 -Action status -Install "$WINSTALL" -Instance "$INST" | grep -q '^running'; then
        die "$INST is already running - stop it first: ./track.sh $TRACK stop"
    fi
    ensure_cfg
    # Every instance runs at 640x360 (LAUNCH-POLICY.md) - the tracks are checked from the log and
    # from the mod's own off-screen captures, so window pixels are pure cost. TRACK_W/TRACK_H
    # override for a one-off that genuinely needs more.
    TW=${TRACK_W:-640}; TH=${TRACK_H:-360}
    read -r DESK_W DESK_H < <(desktop_size)
    WX=$(( (SLOT % 2) * (DESK_W / 3) )); WY=$(( (SLOT / 2) * (DESK_H / 2) ))
    set_window_cfg "$WX" "$WY" "$TW" "$TH"
    echo "slot $SLOT: ${TW}x${TH} at (${WX},${WY}) on ${DESK_W}x${DESK_H}, instance '$INST'"

    out=$(remote_ps track-instance.ps1 -Action launch -Install "$WINSTALL" -Instance "$INST" \
              -ScreenWidth "$TW" -ScreenHeight "$TH")
    echo "$out"
    if echo "$out" | grep -q '^already-running'; then
        die "$INST is already running - stop it first: ./track.sh $TRACK stop"
    fi
    echo "$out" | grep -q '^started' || die "launch failed (see above)"
    echo "log: ./track.sh $TRACK log"
    ;;

log)
    [ -f "$LOG" ] || die "no log at $LOG (has $INST ever run?)"
    arg="${3:-}"
    if [ "$arg" = "follow" ] || [ "$arg" = "-f" ]; then
        tail -n "${LINES:-40}" -f "$LOG"
    else
        tail -n "${arg:-${LINES:-60}}" "$LOG"
    fi
    ;;

stop)
    check_install
    remote_ps track-instance.ps1 -Action stop -Install "$WINSTALL" -Instance "$INST"
    ;;

status)
    check_install
    remote_ps track-instance.ps1 -Action status -Install "$WINSTALL" -Instance "$INST"
    if [ -f "$REMOTE_HOT/AlterumModelSwap.Hot.dll" ]; then
        echo "hot dll: $(stat -c %y "$REMOTE_HOT/AlterumModelSwap.Hot.dll" | cut -d. -f1) ($(stat -c %s "$REMOTE_HOT/AlterumModelSwap.Hot.dll") bytes)"
    else
        echo "hot dll: absent (nothing will load - run build + deploy)"
    fi
    if [ -f "$LOG" ]; then
        echo "--- last [hot] lines (most recent run) ---"
        grep -E '\[hot\]' "$LOG" | tail -5 || true
    fi
    ;;

cfg)
    check_install
    ensure_cfg
    echo "$CFG"
    ;;

shot)
    sync_win
    png="$WIN/desktop.png"
    rm -f "$png"
    remote_ps run-helper.ps1 -Script "$WINT\\desktop-shot.ps1" >/dev/null 2>&1
    for _ in $(seq 1 40); do [ -f "$png" ] && break; sleep 0.5; done
    [ -f "$png" ] || die "screenshot helper produced no file"
    mkdir -p "$HOME/assets/monorepo/alterum-data/tracks/$TRACK/shots"
    out="$HOME/assets/monorepo/alterum-data/tracks/$TRACK/shots/$(date +%Y%m%d-%H%M%S).png"
    cp "$png" "$out" || die "could not copy screenshot"
    echo "desktop screenshot -> $out"
    ;;

""|-h|--help|help) usage 0 ;;
*) echo "unknown command '$CMD'" >&2; usage 1 ;;
esac
