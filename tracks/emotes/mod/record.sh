#!/usr/bin/env bash
# Record one emote demo clip on THIS track's install (C:\AlterumT2), locked-desktop safe.
#
#   ./record.sh <clip> <repeats> <tag> [seconds] [dist] [fov] [lift] [w] [h]
#
# Two request files drive the running instance, no keys:
#   clip.request   (ClipRecorder) wall-clock frame recording, showcase view
#   emote.request  (EmoteRuntime) start the named clip, hold N loops, stop
# The emote is started ~0.5 s after the recorder opens, so the video has a lead-in before the
# pose takes over and a tail after it releases. Prints the recorder's own finish line (wall
# clock -> achieved fps) and the emote request lines from the same window.
set -uo pipefail

SCREENS='/mnt/c/AlterumT2/BepInEx/screens/t2'
LOG='/mnt/c/AlterumT2/BepInEx/LogOutput.log'
LEAD="${LEAD:-0.55}"

clip=${1:?clip name}
repeats=${2:-1}
tag=${3:?tag}
seconds=${4:-3}
dist=${5:-2.4}
fov=${6:-40}
lift=${7:-0.28}
w=${8:-640}
h=${9:-360}
fps=${FPS:-15}

[ -d "$SCREENS" ] || { echo "no screens dir $SCREENS (is C:\\AlterumT2 running?)" >&2; exit 1; }
rm -rf "$SCREENS/$tag"

printf 'tag=%s\nseconds=%s\nfps=%s\nview=showcase\nyaw=auto\ndist=%s\nfov=%s\nlift=%s\nwidth=%s\nheight=%s\n' \
    "$tag" "$seconds" "$fps" "$dist" "$fov" "$lift" "$w" "$h" > "$SCREENS/clip.request"
echo "record: clip.request tag=$tag ${w}x${h} fps=$fps seconds=$seconds dist=$dist fov=$fov lift=$lift"

deadline=$(( $(date +%s) + 30 ))
until grep -q "\[clip\] start tag='$tag'" "$LOG" 2>/dev/null; do
    [ "$(date +%s)" -lt "$deadline" ] || { echo "record: recorder never started" >&2; exit 1; }
    sleep 0.2
done
echo "record: recorder started, lead-in ${LEAD}s"
sleep "$LEAD"

printf 'clip=%s\nrepeats=%s\n' "$clip" "$repeats" > "$SCREENS/emote.request"
echo "record: emote.request clip=$clip repeats=$repeats"

deadline=$(( $(date +%s) + 120 ))
until grep -qE "\[clip\] (finish|stopped) tag='$tag'" "$LOG" 2>/dev/null; do
    [ "$(date +%s)" -lt "$deadline" ] || { echo "record: recorder never finished" >&2; exit 1; }
    sleep 0.5
done

echo "--- recorder ---"
grep -E "\[clip\] (start|showcase|finish|stopped) tag='$tag'|\[clip\] $tag frame" "$LOG" | sed 's/^.*\[clip\]/[clip]/' | tail -16
echo "--- emote (same window) ---"
grep -E "\[emote(:request)?\]|\[emote:sample\]" "$LOG" | tail -12 | sed 's/^.*\[emote/[emote/'
