#!/usr/bin/env bash
# One-shot locked-desktop demo clip: deploy the hot assembly, drop a clip.request, wait for the
# recorder to finish, verify the frames numerically, and assemble an MP4.
#
#   ./clip.sh A                                  # blend defaults: showcase, yaw=auto, 3s @15fps
#   ./clip.sh B --tag emotes_griddy --seconds 6 --fps 15 --view showcase --yaw auto --dist 2.6
#   ./clip.sh A --view main --seconds 2 --no-build
#
# The instance must already be running (this script never starts or stops the game); deploy is a
# hot reload, which is the point. Exit codes: 0 ok, 1 recorder/verification failure.
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
build=1
instance=""
tag="clip"
seconds="3"
fps="15"
view="showcase"
yaw="auto"
dist=""
width=""
height=""

usage() {
    sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
    exit 1
}

while [ $# -gt 0 ]; do
    case "$1" in
        A|B|a|b) instance=$(printf '%s' "$1" | tr '[:lower:]' '[:upper:]') ;;
        --tag) tag=$2; shift ;;
        --seconds) seconds=$2; shift ;;
        --fps) fps=$2; shift ;;
        --view) view=$2; shift ;;
        --yaw) yaw=$2; shift ;;
        --dist) dist=$2; shift ;;
        --width) width=$2; shift ;;
        --height) height=$2; shift ;;
        --no-build) build=0 ;;
        -h|--help) usage ;;
        *) printf 'clip.sh: unknown argument %s\n' "$1" >&2; usage ;;
    esac
    shift
done
[ -n "$instance" ] || usage

case "$instance" in
    A) game="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"; screens="$game/BepInEx/screens";         hot="$game/BepInEx/hot";    log="$game/BepInEx/LogOutput.log" ;;
    B) game="/mnt/c/AlterumB";                                          screens="$game/BepInEx/screens/b";    hot="$game/BepInEx/hot/b";  log="$game/BepInEx/logs/b.log" ;;
esac

[ -d "$game" ] || { printf 'clip.sh: install not found: %s\n' "$game" >&2; exit 1; }
[ -d "$screens" ] || { printf 'clip.sh: screens dir not found: %s (is the game running?)\n' "$screens" >&2; exit 1; }

if [ "$build" = 1 ]; then
    printf 'clip.sh: building hot assembly\n'
    ( cd "$here/tracks/merged/mod/AlterumModelSwap.Hot" && \
      DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
      dotnet build -c Release -v q --nologo 2>&1 | grep -E 'error CS' ) && exit 1
    dll="$here/tracks/merged/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
    [ -f "$dll" ] || { printf 'clip.sh: build produced no dll\n' >&2; exit 1; }
    # atomic: the host reloads on a file change and would otherwise read a half-written assembly
    mkdir -p "$hot" && cp "$dll" "$hot/.AlterumModelSwap.Hot.dll.tmp" && \
        mv -f "$hot/.AlterumModelSwap.Hot.dll.tmp" "$hot/AlterumModelSwap.Hot.dll" || \
        { printf 'clip.sh: deploy failed\n' >&2; exit 1; }
    printf 'clip.sh: deployed %s -> %s\n' "$(md5sum "$dll" | cut -d' ' -f1)" "$hot"
    sleep 1
fi

req="$screens/clip.request"
{
    printf 'tag=%s\nseconds=%s\nfps=%s\nview=%s\nyaw=%s\n' "$tag" "$seconds" "$fps" "$view" "$yaw"
    [ -n "$dist" ]   && printf 'dist=%s\n' "$dist"
    [ -n "$width" ]  && printf 'width=%s\n' "$width"
    [ -n "$height" ] && printf 'height=%s\n' "$height"
} > "$req"
printf 'clip.sh: dropped %s\n' "$req"

# Wait for the recorder itself, not for a guessed delay.
start_lines=$(grep -c '\[clip\] start' "$log" 2>/dev/null); start_lines=${start_lines:-0}
deadline=$(( $(date +%s) + 60 ))
result=""
while [ "$(date +%s)" -lt "$deadline" ]; do
    sleep 1
    now_lines=$(grep -c '\[clip\] start' "$log" 2>/dev/null); now_lines=${now_lines:-0}
    if [ "$now_lines" -gt "$start_lines" ]; then
        result=$(grep -E '\[clip\] (finish|stopped|start|showcase|mask) ' "$log" | tail -n 8)
        grep -qE '\[clip\] (finish|stopped)' "$log" && break
    fi
done

printf 'clip.sh: recorder log:\n'
printf '%s\n' "${result:-(no [clip] start line — is the instance running?)}"

frames="$screens/$tag"
if [ ! -d "$frames" ]; then
    printf 'clip.sh: no frames dir at %s\n' "$frames" >&2
    exit 1
fi

printf 'clip.sh: verifying frames\n'
python3 "$here/verify/clip_check.py" "$frames" || { printf 'clip.sh: frame verification failed\n' >&2; exit 1; }

mkdir -p "$here/clips"
out="$here/clips/${tag}-${instance}.mp4"
printf 'clip.sh: assembling %s\n' "$out"
"$here/make_video.sh" "$frames" "$out" "$fps"
