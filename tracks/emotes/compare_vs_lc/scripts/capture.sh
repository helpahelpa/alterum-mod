#!/usr/bin/env bash
# Matched-phase capture of the port: freezes one clip at a list of normalised phases and reads
# back one PNG per phase from the mod's LC-shaped private camera (no desktop is touched).
#   capture.sh <Clip> <tag> <camyaw> <phases comma list>
set -u
SCREENS=/mnt/c/AlterumT2/BepInEx/screens/t2
CLIP="${1:?clip}"; TAG="${2:?tag}"; YAW="${3:-25}"; PHASES="${4:?phases}"
N=$(awk -F, '{print NF}' <<<"$PHASES")
rm -f "$SCREENS"/emote_cap_"${CLIP}_${TAG}"_*.png
# EXTRA: newline-separated request lines for the A/B switches (armconform=0, leghint=0, ...)
printf 'clip=%s\nrepeats=0\ncap=%s\nphases=%s\ncamyaw=%s\ncamdist=%s\ncamaim=%s\ncamfov=42\ndump=1\n%s\n' \
  "$CLIP" "$TAG" "$PHASES" "$YAW" "${CAMDIST:-4.2}" "${CAMAIM:-0.85}" "${EXTRA:-}" > "$SCREENS/emote.request"
for _ in $(seq 1 80); do
  n=$(ls "$SCREENS"/emote_cap_"${CLIP}_${TAG}"_*.png 2>/dev/null | wc -l)
  [ "$n" -ge "$N" ] && break
  sleep 0.25
done
sleep 0.4
# stop the clip and let the fade-out finish so the next request starts clean
printf 'stop=1\n' > "$SCREENS/emote.request"
sleep 0.6
echo "$CLIP $TAG yaw=$YAW captured=$(ls "$SCREENS"/emote_cap_"${CLIP}_${TAG}"_*.png 2>/dev/null | wc -l)/$N"
