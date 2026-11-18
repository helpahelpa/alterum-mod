#!/bin/bash
# Item 2: prove the CLIENT's own body is re-created after a death+respawn.
# host = C:\AlterumPinned (production build), client = C:\AlterumB.
# Custom wanted entry (Sahur Classic) + hat (cosmetics.txt) + enhancement profile (ChestEnabled).
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DLL="$HERE/tracks/merged/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
A=/mnt/c/AlterumPinned
B=/mnt/c/AlterumB
ACFG=$A/BepInEx/config/com.user0.alterum.modelswap.cfg
BCFG=$B/BepInEx/config/com.user0.alterum.modelswap.b.cfg
ALOG=$A/BepInEx/LogOutput.log
BLOG=$B/BepInEx/LogOutput.log
ASCR=$A/BepInEx/screens
BSCR=$B/BepInEx/screens
say() { echo "[$(date +%H:%M:%S)] $*"; }

say "killing every instance"
ssh windows 'Get-Process -Name Alterum -ErrorAction SilentlyContinue | Stop-Process -Force' >/dev/null 2>&1
sleep 3

say "deploying production dll to pin and B"
for d in "$A/BepInEx/hot" "$B/BepInEx/hot/b"; do
  mkdir -p "$d"; cp "$DLL" "$d/.tmp" && mv -f "$d/.tmp" "$d/AlterumModelSwap.Hot.dll"
done
md5sum "$DLL" | sed 's/^/  dll md5 /'
rm -f "$A/BepInEx/config/alterum-random-model-onjoin.txt" "$B/BepInEx/config/alterum-random-model-onjoin.txt"
rm -f "$ALOG" "$BLOG"
mkdir -p "$ASCR" "$BSCR"

# no death from other causes: keep the client's enhancement profile + hat state as deployed
say "hat state: $(cat $B/BepInEx/config/cosmetics.txt 2>/dev/null)  chest=$(grep -m1 ChestEnabled $B/BepInEx/config/com.user0.alterum.enhancements.cfg 2>/dev/null)"

python3 - "$ACFG" "$BCFG" <<'PY'
import sys
a,b=sys.argv[1],sys.argv[2]
def edit(p, seq):
    s=open(p,'rb').read().decode('utf-8-sig').splitlines()
    for i,l in enumerate(s):
        if l.startswith('ClickSequence = '): s[i]='ClickSequence = '+seq
        if l.startswith('ClickFirstDelaySeconds = '): s[i]='ClickFirstDelaySeconds = 6'
        if l.startswith('ClickStepDelaySeconds = '): s[i]='ClickStepDelaySeconds = 4'
        if l.startswith('Quiet = '): s[i]='Quiet = false'
        if l.startswith('Screenshots = '): s[i]='Screenshots = false'
    open(p,'w',encoding='utf-8').write('\n'.join(s)+'\n')
edit(a,'Create Lobby Menu Button|@LobbyPrivate|Lobby Creator/Vertical Sectors/Buttons/Gothic|@LobbyCode|@Peers')
edit(b,'')
PY

say "launching host (pin)"
/tmp/winrun.sh "$HERE/win/run-game.ps1" >/dev/null 2>&1
for i in $(seq 1 45); do
  sleep 2
  grep -q 'code=[A-Z0-9]\{4,\}' "$ALOG" 2>/dev/null && break
done
CODE=$(grep -oE 'code=[A-Z0-9]+' "$ALOG" 2>/dev/null | tail -1 | cut -d= -f2)
say "lobby code='$CODE'"
[ -z "$CODE" ] && { say "FAIL no lobby code"; tail -20 "$ALOG"; exit 1; }

python3 - "$BCFG" "$CODE" <<'PY'
import sys
p,code=sys.argv[1],sys.argv[2]
s=open(p,'rb').read().decode('utf-8-sig').splitlines()
for i,l in enumerate(s):
    if l.startswith('ClickSequence = '):
        s[i]='ClickSequence = Code Menu Button|@JoinLobby=%s|Connect via code/Gothic Yellow Button Variant|@Peers' % code
open(p,'w',encoding='utf-8').write('\n'.join(s)+'\n')
PY

say "launching client (B)"
cp "$HERE/win/run-second.ps1" /mnt/c/Temp/alterum/run-second.ps1
ssh windows 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Temp\alterum\run-second.ps1 -GameDir C:\AlterumB -GameArgs ''"-screen-fullscreen 0 -screen-width 640 -screen-height 360 -alterumInstance=b"''' 2>&1 | grep -vE 'post-quantum|store now|openssh|vulnerable|WARNING: conn' | tail -1
for i in $(seq 1 45); do
  sleep 2
  grep -q "\[scene\] -> 'Lobby'" "$BLOG" 2>/dev/null && break
done
sleep 8
say "B scene: $(grep -o "\[scene\] -> '[A-Za-z]*'" "$BLOG" | tail -1)"
say "clients seen by host: $(grep -o 'SetBaseModelIndex(client=[0-9]*' "$ALOG" | tail -3 | tr '\n' ' ')"

# ---- custom wanted entry on the CLIENT (Sahur Classic: a custom roster entry, verified hat anchor)
say "client: select custom wanted entry (classic)"
echo "wardrobe=select=classic" > "$BSCR/sahur.request"
sleep 6
grep -E "sahur:wardrobe|wanted|sahur:bind|\[mapbody\]" "$BLOG" | tail -6 | sed 's/^/  B| /'

say "client: capture before (lobby)"
echo "before-lobby" > "$BSCR/shot.request"
sleep 4

# ---- into the map
say "host: ui.request startmap"
mkdir -p "$ASCR"; echo "startmap" > "$ASCR/ui.request"
for i in $(seq 1 60); do sleep 2; grep -q "\[scene\] -> 'Lair'" "$BLOG" 2>/dev/null && break; done
sleep 10
say "A scene: $(grep -o "\[scene\] -> '[A-Za-z]*'" "$ALOG" | tail -1)  B scene: $(grep -o "\[scene\] -> '[A-Za-z]*'" "$BLOG" | tail -1)"
grep -E "\[mapbody\]" "$BLOG" | tail -3 | sed 's/^/  B| /'

say "client: capture map-before"
echo "map-before" > "$BSCR/shot.request"
sleep 4

CID=$(grep -oE '^.*\[join:trace\].*localClient=[0-9]+' "$BLOG" | grep -oE 'localClient=[0-9]+' | tail -1 | cut -d= -f2)
[ -z "${CID:-}" ] && CID=$(grep -oE 'client=[0-9]+' "$BLOG" | grep -oE '[0-9]+' | sort -n | tail -1)
say "client id = '$CID'"

# ---- kill through the game's own damage path (host-side Health.Damage)
say "host: killremote=$CID"
echo "killremote=$CID" > "$ASCR/reel.request"
sleep 6
say "client: capture dead"
echo "dead" > "$BSCR/shot.request"
sleep 4
grep -E "hurtremote|Health|dead" "$ALOG" | tail -3 | sed 's/^/  A| /'
grep -E "\[mapbody\]|dead|hide" "$BLOG" | tail -4 | sed 's/^/  B| /'

# ---- respawn through the game's own respawner
say "host: respawn=$CID"
echo "respawn=$CID" > "$ASCR/reel.request"
sleep 12
say "client: capture after"
echo "after" > "$BSCR/shot.request"
sleep 4
grep -E "\[reel\] respawn|respawner" "$ALOG" | tail -6 | sed 's/^/  A| /'
grep -E "\[mapbody\]|sahur|enh|cosmetic" "$BLOG" | tail -12 | sed 's/^/  B| /'

say "captures:"
ls -la "$BSCR"/*.png 2>/dev/null | tail -6
say "done"
