#!/bin/bash
# Third-person camera / hat-coupling verification, driven entirely through request files.
# Hot reload only: it never restarts or kills the game. Every step writes into the live instance's
# BepInEx/screens/ and the evidence is read back out of BepInEx/LogOutput.log.
#
#   ./tp-verify.sh          run the sequence (leaves the player at the spawn, third person)
#
# Captures land in BepInEx/screens/ (single frames + bodyproof/<tag>_{on,off}.png mask pairs);
# copy them into shots/third-person-camera/ afterwards (see the README there).
# Request files are written to a temp name and renamed: a direct write can be read while it is still
# open, which the module correctly reports as "read failed" and then leaves the mode alone.
set -u
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
LOG="$GAME/BepInEx/LogOutput.log"
S="$GAME/BepInEx/screens"
ORIGIN="${ORIGIN:-5.354,-7.568}"       # the Lobby spawn eye, from the game's own first engage
MIDROOM="${MIDROOM:-2.0}"             # metres forward of the spawn for the "in the room" spot
MARK=0

req()   { local f="$1" v="$2" t="$S/.$1.tmp"; printf '%s' "$v" > "$t" && mv -f "$t" "$S/$f"; }
mark()  { MARK=$(wc -l < "$LOG" 2>/dev/null || echo 0); }
since() { tail -n +$((MARK+1)) "$LOG" | grep -E "$1"; }
pause() { sleep "${1:-2.5}"; }
say()   { echo; echo "=== $*"; }
newlines() { tail -n +$((MARK+1)) "$LOG"; }

# POV is a mode, not a one-shot: retry until the module confirms it read the request and applied it.
pov() {
  local want="$1" word applied
  if [ "$want" = "on" ]; then word="third-person"; applied="third-person ON"; else word="first-person"; applied="third-person OFF"; fi
  for i in 1 2 3; do
    mark; req pov.request "$want"; pause 3
    if newlines | grep -q "\[pov\] request $word"; then
      for j in 1 2 3; do
        if newlines | grep -q "\[pov\] $applied"; then pause 1.5; return 0; fi
        sleep 1
      done
      echo "WARN: pov=$want requested but not confirmed applied"
      return 0
    fi
    echo "WARN: pov=$want request not picked up (attempt $i)"
  done
  echo "ERROR: pov.request could not be delivered"
  return 1
}

mkdir -p "$S" "$S/bodyproof"

say "third person"
pov on || exit 1

say "1. spawn: the candidate the rig picks there"
req cam.request "teleport=to:$ORIGIN"; pause 3
mark; req cam.request stab; pause 7
req cam.request dump; pause 2.5
since "tp-cam:(dump|pick|candidate|no-room|stab)"
mark; req shot.request spawn-tp; pause 2.5
since "\[shot\]"
mark; req screenproof.request $'tag=spawn-tp-body\npair=live\nlayer=14'; pause 2.5
since "bodyview:proof"
mark; req screenproof.request $'tag=spawn-tp-hat\npair=live\nlayer=6'; pause 2.5
since "bodyview:proof"

say "2. ${MIDROOM} m into the room: the natural behind view"
req cam.request "teleport=fwd:$MIDROOM"; pause 3
mark; req cam.request stab; pause 7
req cam.request dump; pause 2.5
since "tp-cam:(dump|pick|candidate|no-room|stab)"
mark; req shot.request midroom-tp; pause 2.5
since "\[shot\]"
mark; req screenproof.request $'tag=midroom-tp-body\npair=live\nlayer=14'; pause 2.5
req screenproof.request $'tag=midroom-tp-hat\npair=live\nlayer=6'; pause 2.5
since "bodyview:proof"

say "3. first person: no body, no hat, mask back to the pristine bytes"
pov off || exit 1
mark; req cam.request dump; pause 2.5
since "tp-cam:(dump|pick|restore)"
mark; req shot.request fp-midroom; pause 2.5
since "\[shot\]"
mark; req screenproof.request $'tag=fp-body\npair=live\nlayer=14'; pause 2.5
req screenproof.request $'tag=fp-hat\npair=live\nlayer=6'; pause 2.5
since "bodyview:proof|bodyview:screen\] pov=FirstPerson|cosmetic:layer\] screen policy"

say "4. mirror: both bits stay on the mirror mask in both POVs"
# The player is put on the mirror camera's own axis (3 m out), so the reflection has a body and a hat
# to show. The reflection is captured through the game camera at the glass: rendering the mirror
# camera itself on demand comes back black (URP does not re-render that disabled camera off-frame),
# so its mask is taken from the proof header line instead.
req cam.request "teleport=to:1.58,-10.09"; pause 3
pov on || exit 1
mark; req screenproof.request $'tag=mirror-cam-tp\nsource=mirror'; pause 2.5
since "bodyview:proof\] tag='mirror-cam-tp'"
mark; req screenproof.request $'tag=mirror-glass-tp\nsource=game\neye=0.72,1.75,-11.06\nlookat=-0.328,1.15,-12.273'; pause 2.5
since "bodyview:proof|bodyview:screen\] pov=ThirdPerson"
pov off || exit 1
mark; req screenproof.request $'tag=mirror-cam-fp\nsource=mirror'; pause 2.5
since "bodyview:proof\] tag='mirror-cam-fp'"
mark; req screenproof.request $'tag=mirror-glass-fp\nsource=game\neye=0.72,1.75,-11.06\nlookat=-0.328,1.15,-12.273'; pause 2.5
since "bodyview:proof|bodyview:screen\] pov=FirstPerson|cosmetic:layer\] screen policy"

say "5. back to the spawn, third person (left running for the user)"
req cam.request "teleport=to:$ORIGIN"; pause 3
pov on || exit 1
mark; req shot.request final-spawn-tp; pause 2.5
since "\[shot\]"
mark; req cam.request dump; pause 2.5
since "tp-cam:(dump|pick|candidate)"
mark; req screenproof.request $'tag=final-spawn-tp-body\npair=live\nlayer=14'; pause 2.5
req screenproof.request $'tag=final-spawn-tp-hat\npair=live\nlayer=6'; pause 2.5
since "bodyview:proof"

say "mask summary (last 14 body/cosmetic policy lines)"
grep -E "bodyview:screen\] pov=|cosmetic:layer\] screen policy" "$LOG" | tail -14
