#!/bin/bash
# Deploy the current hot build to A (pinned) and B, restart both, A creates a private lobby,
# B joins it by code. Prints the join-relevant log lines from both sides.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
DLL="$HERE/tracks/merged/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
ACFG=/mnt/c/AlterumPinned/BepInEx/config/com.user0.alterum.modelswap.cfg
BCFG=/mnt/c/AlterumB/BepInEx/config/com.user0.alterum.modelswap.b.cfg

ssh windows 'Get-Process Alterum -ErrorAction SilentlyContinue | Stop-Process -Force' >/dev/null 2>&1 || true
sleep 2

for d in /mnt/c/AlterumPinned/BepInEx/hot /mnt/c/AlterumB/BepInEx/hot/b; do
  mkdir -p "$d"
  cp "$DLL" "$d/.tmp" && mv -f "$d/.tmp" "$d/AlterumModelSwap.Hot.dll"
done
if [ "${KEEP:-0}" != "1" ]; then rm -f /mnt/c/AlterumPinned/BepInEx/config/alterum-random-model-onjoin.txt \
      /mnt/c/AlterumB/BepInEx/config/alterum-random-model-onjoin.txt; fi
rm -f /mnt/c/AlterumPinned/BepInEx/LogOutput.log /mnt/c/AlterumB/BepInEx/LogOutput.log

python3 - "$ACFG" "$BCFG" <<'PY'
import sys
a,b=sys.argv[1],sys.argv[2]
def edit(p, seq, first, step):
    s=open(p,'rb').read().decode('utf-8-sig').splitlines()
    for i,l in enumerate(s):
        if l.startswith('ClickSequence = '): s[i]='ClickSequence = '+seq
        if l.startswith('ClickFirstDelaySeconds = '): s[i]='ClickFirstDelaySeconds = '+str(first)
        if l.startswith('ClickStepDelaySeconds = '): s[i]='ClickStepDelaySeconds = '+str(step)
        if l.startswith('Quiet = '): s[i]='Quiet = false'
        if l.startswith('Screenshots = '): s[i]='Screenshots = false'
    open(p,'w',encoding='utf-8').write('\n'.join(s)+'\n')
edit(a,'Create Lobby Menu Button|@LobbyPrivate|Lobby Creator/Vertical Sectors/Buttons/Gothic|@LobbyCode|@Peers',6,4)
edit(b,'',10,4)
PY

/tmp/winrun.sh "$HERE/win/run-game.ps1" >/dev/null 2>&1
for i in $(seq 1 45); do
  sleep 2
  if grep -q 'code=[A-Z0-9]\{4,\}' /mnt/c/AlterumPinned/BepInEx/LogOutput.log 2>/dev/null; then break; fi
done
CODE=$(grep -oE 'code=[A-Z0-9]+' /mnt/c/AlterumPinned/BepInEx/LogOutput.log | tail -1 | cut -d= -f2)
[ -z "$CODE" ] && { echo "no lobby code"; exit 1; }
echo "lobby code: $CODE"

python3 - "$BCFG" "$CODE" <<'PY'
import sys
p,code=sys.argv[1],sys.argv[2]
s=open(p,'rb').read().decode('utf-8-sig').splitlines()
for i,l in enumerate(s):
    if l.startswith('ClickSequence = '):
        s[i]='ClickSequence = Code Menu Button|@JoinLobby=%s|Connect via code/Gothic Yellow Button Variant|@Peers' % code
open(p,'w',encoding='utf-8').write('\n'.join(s)+'\n')
PY

cp "$HERE/win/run-second.ps1" /mnt/c/Temp/alterum/run-second.ps1
ssh windows 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\Temp\alterum\run-second.ps1 -GameDir C:\AlterumB -GameArgs ''"-screen-fullscreen 0 -screen-width 640 -screen-height 360 -alterumInstance=b"''' 2>&1 | grep -vE 'post-quantum|store now|openssh|vulnerable|WARNING: conn' | tail -1

for i in $(seq 1 45); do
  sleep 2
  if grep -q "\[scene\] -> 'Lobby'" /mnt/c/AlterumB/BepInEx/LogOutput.log 2>/dev/null; then break; fi
done
sleep 8
echo "code=$CODE"
