#!/bin/bash
# Gated clothes toggle ("hide a garment only where skin lies under it") verification, install A.
# Hot reload only: the game is never restarted or killed.
#
#   tools/clothes-gated-verify.sh        full run -> shots/clothes-toggle-gated/
#
# The rig is ProportionShots (propcap.request): freeze + **rebind** pins every mannequin in its
# bind pose, which is the only pose that reproduces exactly across requests (the wardrobe's own
# idle/hair physics keeps moving otherwise, ~1k px of hair noise). Every state of a model is
# therefore captured through the same session, the same camera and the same pose; the toggle is
# the only thing that differs.
#
# Produced in shots/clothes-toggle-gated/:
#   <model>-{clothed,gated,all}-{front,three}.png   frozen ortho rig, model soloed
#   local-{clothed,gated,all}[_mirror].png           screen camera + the mirror's own RenderTexture
#   analysis.txt                                     per-model verdict (see "the test" below)
#   coverage.log                                     the [clothes] gate audit lines of this run
#   state.log                                        the [clothes] state/count lines of this run
#
# The test, measured in analysis.txt instead of by eye:
#   * connected components of the model mask: gated must stay 1 (a floating head, hands or boots
#     is a second component; `all` is the contrast that produces 4-5),
#   * enclose background inside the silhouette ("a hole where a garment was removed"),
#   * pixels differing from `clothed` (0 = the gate hid nothing on that model).
set -u
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
LOG="$GAME/BepInEx/LogOutput.log"
S="$GAME/BepInEx/screens"
OUT="$(cd "$HOME/assets/monorepo/alterum-data/tracks/merged" && pwd)/shots/clothes-toggle-gated"
T0=$(date +%s)

req()   { local f="$1" v="$2" t="$S/.$1.tmp"; printf '%s' "$v" > "$t" && mv -f "$t" "$S/$f"; }
say()   { echo; echo "=== $*"; }
pause() { sleep "${1:-2}"; }
mark()  { MARK=$(wc -l < "$LOG" 2>/dev/null || echo 0); }
since() { tail -n +$((MARK+1)) "$LOG" | grep -aE "$1"; }

newest() {   # newest <dir> <glob> -> newest file written by THIS run
    local dir="$1" pat="$2" f="" i
    for i in $(seq 1 30); do
        f=$(find "$dir" -maxdepth 1 -name "$pat" -newermt "@$T0" -printf '%T@ %p\n' 2>/dev/null |
            sort -nr | head -1 | cut -d' ' -f2-)
        [ -n "$f" ] && { printf '%s' "$f"; return 0; }
        sleep 0.5
    done
    return 1
}

grab()  { local f; f=$(newest "$S" "shot_main_${1}_*.png") || { echo "  MISSING shot '$1'" >&2; return 1; }
          cp -f "$f" "$OUT/$2"; echo "  $2 <- $(basename "$f")"; }
grab_mirror() { local f; f=$(newest "$S/clothes" "${1}_mirror.png") || { echo "  MISSING mirror '$1'" >&2; return 1; }
          cp -f "$f" "$OUT/$2"; echo "  $2 <- $(basename "$f")"; }
# last_block <start-marker> <end-marker>  -> the newest complete block from the live log
last_block() {
    awk -v start="$1" -v end="$2" 'index($0,start){buf=""} {buf=buf $0 "\n"} index($0,end){out=buf} END{printf "%s", out}' "$LOG"
}

grab_prop() { local f; f=$(newest "$S" "shot_main_prop_${1}_${2}_*.png") || { echo "  MISSING prop $1/$2" >&2; return 1; }
          cp -f "$f" "$OUT/$3"; }

# propcap <tag> <request body>  -- waits for THIS tag's completion line
propcap() {
    local tag="$1" body="$2" i
    mark
    req propcap.request "$body"
    for i in $(seq 1 60); do
        sleep 0.5
        tail -n +$((MARK+1)) "$LOG" | grep -aqE "propcap:done.*tag='$tag'|propcap.*ignored|did not" && { sleep 0.4; return 0; }
    done
    echo "  !! propcap '$tag' did not complete" >&2
    return 1
}

capture() {  # capture <tag> <model>   (already frozen+rebound+soloed by the caller)
    propcap "$1" "model=$2
solo=1
tag=$1
views=front,three
width=720
height=1080"
}

# freeze_model <model>  -- pins the bind pose and the framing. MUST be a request of its own: a
# freeze of a different rig calls Release first, which re-activates the solo-hidden models, so a
# tag in the same request would capture the whole wardrobe.
freeze_model() {
    local model="$1" i
    mark
    req propcap.request "model=$model
freeze=1
rebind=1"
    for i in $(seq 1 20); do
        sleep 0.5
        tail -n +$((MARK+1)) "$LOG" | grep -aq "propcap:freeze. model=" && { sleep 0.3; return 0; }
    done
    echo "  !! freeze of '$model' did not report" >&2
    return 1
}

mkdir -p "$OUT" "$S/clothes"
# Clear this run's artifacts but never the README that documents them.
rm -f "$OUT"/*.png "$OUT"/*.txt "$OUT"/*.log

# ---------------------------------------------------------------------------------------------
say "state: reset the capture rig, clothes on, third person at the mirror"
req propcap.request "release=1"; pause 3
mark; req clothes.request "on"; pause 1; since "clothes\] (on|ON)" | tail -1
mark; req pov.request on; pause 2; since "\[pov\] (request|third-person ON)" | tail -1
req cam.request "teleport=to:1.58,-10.09"; pause 3
since "tp-cam:teleport" | tail -1

# ---------------------------------------------------------------------------------------------
say "1. local player (both cameras): clothed / gated / all"
req shot.request local-clothed; pause 2
req clothes.request "mirror=local-clothed"; pause 2
mark; req clothes.request $'gated\nmirror=local-gated'; pause 3
since "clothes\] (gated:|GATED)" | tail -2
req shot.request local-gated; pause 2
mark; req clothes.request $'all\nmirror=local-all'; pause 3
since "clothes\] (all:|ALL)" | tail -1
req shot.request local-all; pause 2
req clothes.request "on"; pause 1

mark; req clothes.request "coverage"; pause 5
last_block "[clothes] gate (" "gate summary" > "$OUT/coverage.log"
echo "  coverage.log: $(wc -l < "$OUT/coverage.log") lines"

# ---------------------------------------------------------------------------------------------
say "2. wardrobe mannequins: clothed vs gated vs all, frozen bind pose, same camera"
MODELS=(
  "3P_ConcreteModel_Male_05_Ryan:ryan"
  "3P_ConcreteModel_Male_04_James:james"
  "3P_ConcreteModel_Male_03:m03"
  "3P_ConcreteModel_Male_02:m02"
  "3P_ConcreteModel_Male_01:m01"
  "3P_ConcreteModel_Female_05_Sarah:sarah"
  "3P_ConcreteModel_Female_04_Kiona:kiona"
  "3P_ConcreteModel_Female_03_Diana:diana"
  "3P_ConcreteModel_Female_02:f02"
)
for M in "${MODELS[@]}"; do
    model="${M%%:*}"; name="${M##*:}"
    echo "--- $name"
    freeze_model "$model"
    capture "$name-clothed" "$model"
    mark; req clothes.request "gated"; pause 2
    since "clothes\] gate summary" | tail -1
    capture "$name-gated" "$model"
    mark; req clothes.request "all"; pause 1
    capture "$name-all" "$model"
    req clothes.request "on"; pause 1
    for view in front three; do
        grab_prop "$view" "$name-clothed" "$name-clothed-$view.png"
        grab_prop "$view" "$name-gated"   "$name-gated-$view.png"
        grab_prop "$view" "$name-all"     "$name-all-$view.png"
    done
done

# ---------------------------------------------------------------------------------------------
# The wardrobe's `Female_01` is the same prefab as the local player's body, and it is not the
# first match for its own name (the local player is). Capture that prefab through the probe's own
# spawn path instead: it is also the "a model appeared and must be gated" case (42 garments then).
say "3. Female_01 prefab (spawned capture body) - the same model as the local player's"
req propcap.request "release=1"; pause 2
propcap "spawn-f01" "spawn=Female_1
freeze=1
rebind=1
views=front
width=720
height=1080"
capture "f01-clothed" "PropCaptureBody"
mark; req clothes.request "gated"; pause 2
since "clothes\] (gate summary|gated:)" | tail -2
capture "f01-gated" "PropCaptureBody"
mark; req clothes.request "all"; pause 1
capture "f01-all" "PropCaptureBody"
req clothes.request "on"; pause 1
for view in front three; do
    grab_prop "$view" "f01-clothed" "f01-clothed-$view.png"
    grab_prop "$view" "f01-gated"   "f01-gated-$view.png"
    grab_prop "$view" "f01-all"     "f01-all-$view.png"
done
req propcap.request "release=1"; pause 3

# ---------------------------------------------------------------------------------------------
say "4. captures: local player"
grab local-clothed local-clothed.png
grab local-gated   local-gated.png
grab local-all     local-all.png
grab_mirror local-clothed local-clothed_mirror.png
grab_mirror local-gated   local-gated_mirror.png
grab_mirror local-all     local-all_mirror.png

# ---------------------------------------------------------------------------------------------
say "5. analysis: connected components, enclosed holes, pixels vs clothed"
python3 - "$OUT" <<'PY' | tee "$OUT/analysis.txt"
import os, sys
from collections import deque
import numpy as np
from PIL import Image

out = sys.argv[1]
BG = np.array([26, 28, 33])

def mask(path):
    im = np.asarray(Image.open(path).convert('RGB'), dtype=int)
    return (np.abs(im - BG).sum(axis=2) > 2)

def components(m, min_area=500):
    H, W = m.shape
    lab = np.zeros((H, W), bool)
    sizes = []
    for y in range(H):
        for x in range(W):
            if not m[y, x] or lab[y, x]:
                continue
            n = 0
            dq = deque([(y, x)]); lab[y, x] = True
            while dq:
                cy, cx = dq.popleft(); n += 1
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        ny, nx = cy + dy, cx + dx
                        if 0 <= ny < H and 0 <= nx < W and m[ny, nx] and not lab[ny, nx]:
                            lab[ny, nx] = True; dq.append((ny, nx))
            sizes.append(n)
    return sorted((s for s in sizes if s >= min_area), reverse=True)

def enclosed_background(m):
    """Background pixels NOT reachable from the image border = holes in the silhouette."""
    H, W = m.shape
    bg = ~m
    seen = np.zeros((H, W), bool)
    dq = deque()
    for x in range(W):
        for y in (0, H - 1):
            if bg[y, x] and not seen[y, x]:
                seen[y, x] = True; dq.append((y, x))
    for y in range(H):
        for x in (0, W - 1):
            if bg[y, x] and not seen[y, x]:
                seen[y, x] = True; dq.append((y, x))
    while dq:
        cy, cx = dq.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = cy + dy, cx + dx
            if 0 <= ny < H and 0 <= nx < W and bg[ny, nx] and not seen[ny, nx]:
                seen[ny, nx] = True; dq.append((ny, nx))
    hole = bg & ~seen
    return int(hole.sum()), components(hole, 30)[:3]

def diff_px(a, b):
    ia = np.asarray(Image.open(a).convert('RGB'), dtype=int)
    ib = np.asarray(Image.open(b).convert('RGB'), dtype=int)
    if ia.shape != ib.shape:
        return -1, None
    d = np.abs(ia - ib).sum(axis=2) > 40
    ys, xs = np.where(d)
    box = None if len(ys) == 0 else (int(ys.min()), int(ys.max()), int(xs.min()), int(xs.max()))
    return int(d.sum()), box

models = []
for f in sorted(os.listdir(out)):
    if f.endswith('-clothed-front.png'):
        models.append(f[:-len('-clothed-front.png')])

print('per-model verdict (front view, 720x1080, frozen bind pose)')
print('%-8s %-26s %-26s %-26s %-9s %-20s %s' % ('model', 'clothed(components)', 'gated(components)', 'all(components)', 'px vs clothed', 'changed bbox y/x', 'holes clothed->gated'))
ok = True
for stem in models:
    c = os.path.join(out, stem + '-clothed-front.png')
    g = os.path.join(out, stem + '-gated-front.png')
    a = os.path.join(out, stem + '-all-front.png')
    mc, mg, ma = mask(c), mask(g), mask(a)
    cc, cg, ca = components(mc), components(mg), components(ma)
    d, box = diff_px(c, g)
    hc, _ = enclosed_background(mc)
    hg, hic = enclosed_background(mg)
    hydro = hg - hc
    good = (len(cg) <= len(cc) and len(cg) == 1)
    ok = ok and good
    print('%-8s %-26s %-26s %-26s %-9d %-20s %d -> %d (%+d) %s' % (
        stem, '%d %s' % (len(cc), cc[:2]), '%d %s' % (len(cg), cg[:2]), '%d %s' % (len(ca), ca[:3]),
        d, 'none' if box is None else 'y%d-%d x%d-%d' % box, hc, hg, hydro, 'OK' if good else 'FAIL'))
print()
print('verdict: gated keeps every model one piece: %s' % ('YES' if ok else 'NO'))
print('(component lists are the largest areas in px; a floating head/hand/boot is an extra entry.')
print(' holes = background enclosed by the silhouette; +0 means nothing new was opened.)')
print()
print('local player (screen camera + the mirror RT):')
for tag in ('local-clothed', 'local-gated', 'local-all'):
    for suffix in ('.png', '_mirror.png'):
        p = os.path.join(out, tag + suffix)
        print('  %-28s %s' % (os.path.basename(p), 'present' if os.path.exists(p) else 'MISSING'))
mg = mask(os.path.join(out, 'local-gated.png'))
mc = mask(os.path.join(out, 'local-clothed.png'))
_d, _ = diff_px(os.path.join(out, 'local-clothed.png'), os.path.join(out, 'local-gated.png'))
print('  screen  clothed components=%d gated components=%d  gated-clothed px=%d' % (
    len(components(mc)), len(components(mg)), _d))
print('  (the local player is idle-animating between the two shots, so a pixel diff is expected; the')
print('   garment evidence for this prefab is the frozen `f01-*` pair above.)')
PY

# ---------------------------------------------------------------------------------------------
say "6. reversibility + a reload starts clothed"
mark; req clothes.request "all";  pause 1; since "clothes\] (all:|ALL)" | tail -1
req clothes.request "gated"; pause 1; since "clothes\] (kept |gated:)" | tail -2
req clothes.request "on";    pause 1; since "clothes\] (on: restored|ON)" | tail -1
req clothes.request "gated"; pause 1; since "clothes\] gated:" | tail -1
req clothes.request "on";    pause 1; since "clothes\] on: restored" | tail -1
mark; req clothes.request "all"; pause 1; since "clothes\] all:" | tail -1

mark
cp -f "$(cd "$(dirname "$0")/.." && pwd)/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll" "$GAME/BepInEx/hot/.AlterumModelSwap.Hot.dll.tmp"
mv -f "$GAME/BepInEx/hot/.AlterumModelSwap.Hot.dll.tmp" "$GAME/BepInEx/hot/AlterumModelSwap.Hot.dll"
pause 14
since "\[hot\] generation|clothes\] (ready|dispose|set:|state)" | tail -6
mark; req clothes.request "state"; pause 2; since "clothes\] state" | tail -1
req shot.request local-reload-clothed; pause 3
req clothes.request "mirror=local-reload-clothed"; pause 3
grab local-reload-clothed local-reload-clothed.png
grab_mirror local-reload-clothed local-reload-clothed_mirror.png

# ---------------------------------------------------------------------------------------------
say "7. log gates"
mark
req clothes.request "coverage"; pause 5
last_block "[clothes] gate (" "gate summary" > "$OUT/coverage.log"
grep -a "clothes\]" "$LOG" | tail -40 > "$OUT/state.log"
mark
req clothes.request "dump"; pause 5
last_block "[clothes] classification (" "[clothes] counts:" > "$OUT/renderer-table.log"
echo "  state.log $(wc -l < "$OUT/state.log") lines, renderer-table.log $(wc -l < "$OUT/renderer-table.log") lines"
echo "  [Error] lines : $(grep -ac '\[Error' "$LOG" || true)"
echo "  SWAP lines    : $(grep -ac 'SWAP' "$LOG" || true)"
grep -a "mirror:body-check" "$LOG" | tail -1
grep -a "chest:apply" "$LOG" | tail -2
echo
echo "done in $(( $(date +%s) - T0 ))s; captures in $OUT"
