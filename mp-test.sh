#!/bin/bash
# Drives a 2-instance gbe multiplayer session:
#   A (the PINNED install C:\AlterumPinned) creates a private lobby, B (C:\AlterumB) joins by code.
set -e
# ALTERUM_GAME overrides A; see pinned/UPDATE-POLICY.md.
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
B="/mnt/c/AlterumB"
HERE="$(cd "$(dirname "$0")" && pwd)"
MODE="${MODE:-quiet}"   # quiet = off-screen + screenshots, headed = visible

killall_alterum() {
  cat > /tmp/pskill.txt <<'PS'
Get-Process -Name Alterum -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
PS
  /tmp/pwsh.py < /tmp/pskill.txt >/dev/null 2>&1 || true
}

say() { echo "[mp] $*"; }

killall_alterum
say "deploying current hot build to both"
# The merged tree only: the old shared mod/ tree is the swap-era build and cannot load against the
# merged host (see mod/STALE-DO-NOT-DEPLOY.md). Atomic copy, so the host never reloads a partial dll.
HOT_DLL="$HERE/tracks/merged/mod/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll"
[ -f "$HOT_DLL" ] || { echo "no merged hot build at $HOT_DLL" >&2; exit 1; }
for d in "$GAME/BepInEx/hot" "$B/BepInEx/hot/b"; do
  mkdir -p "$d"
  cp "$HOT_DLL" "$d/.AlterumModelSwap.Hot.dll.tmp" && mv -f "$d/.AlterumModelSwap.Hot.dll.tmp" "$d/AlterumModelSwap.Hot.dll"
done

for f in "$GAME/BepInEx/config/com.user0.alterum.modelswap.cfg" "$B/BepInEx/config/com.user0.alterum.modelswap.b.cfg"; do
  [ -f "$f" ] || continue
  sed -i "s#^Quiet = .*#Quiet = $([ "$MODE" = headed ] && echo false || echo true)#" "$f"
  sed -i "s#^ForceWindowed = .*#ForceWindowed = true#" "$f"
  sed -i "s#^Screenshots = .*#Screenshots = true#" "$f"
  sed -i "s#^ScreenshotIntervalSeconds = .*#ScreenshotIntervalSeconds = 4#" "$f"
done

# two 640x360 windows (LAUNCH-POLICY.md): low-res keeps two live instances cheap
for pair in "$GAME/BepInEx/config/com.user0.alterum.modelswap.cfg:0" "$B/BepInEx/config/com.user0.alterum.modelswap.b.cfg:960"; do
  f="${pair%%:*}"; x="${pair##*:}"
  [ -f "$f" ] || continue
  sed -i "s#^QuietWidth = .*#QuietWidth = 640#" "$f"
  sed -i "s#^QuietHeight = .*#QuietHeight = 360#" "$f"
  sed -i "s#^WindowX = .*#WindowX = $x#" "$f"
  sed -i "s#^WindowY = .*#WindowY = 0#" "$f"
done

say "A: creating private lobby"
sed -i 's#^ClickSequence = .*#ClickSequence = Create Lobby Menu Button|@LobbyPrivate|Lobby Creator/Vertical Sectors/Buttons/Gothic|@LobbyCode|@Peers#' \
  "$GAME/BepInEx/config/com.user0.alterum.modelswap.cfg"
rm -f "$GAME/BepInEx/LogOutput.log"
if [ "$MODE" = headed ]; then /tmp/winrun.sh "$HERE/win/run-game.ps1" >/dev/null 2>&1; else /tmp/winrun.sh "$HERE/win/run-quiet.ps1" >/dev/null 2>&1; fi
for i in $(seq 1 45); do
  sleep 2
  if grep -q 'code=[A-Z0-9]\{4,\}' "$GAME/BepInEx/LogOutput.log" 2>/dev/null; then break; fi
  if grep -q 'NOT creating a lobby' "$GAME/BepInEx/LogOutput.log" 2>/dev/null; then say "A failed to create"; exit 1; fi
done
CODE=$(grep -oE 'code=[A-Z0-9]+' "$GAME/BepInEx/LogOutput.log" | tail -1 | cut -d= -f2)
[ -z "$CODE" ] && { say "no lobby code from A"; exit 1; }
say "A lobby code: $CODE"

say "B: joining $CODE"
sed -i "s#^ClickSequence = .*#ClickSequence = Code Menu Button|@JoinLobby=$CODE|Connect via code/Gothic Yellow Button Variant|@Peers#" \
  "$B/BepInEx/config/com.user0.alterum.modelswap.b.cfg"
rm -f "$B/BepInEx/LogOutput.log"
ssh windows powershell -NoProfile -ExecutionPolicy Bypass -File "C:\\Windows\\Temp\\alterum\\run-second.ps1" \
  -GameDir "C:\\AlterumB" -GameArgs '"-screen-fullscreen 0 -screen-width 640 -screen-height 360 -alterumInstance=b"' 2>&1 \
  | grep -vE 'post-quantum|store now|openssh|vulnerable|WARNING: conn'

for i in $(seq 1 40); do
  sleep 2
  if grep -qE "\[scene\] -> 'Lobby'" "$B/BepInEx/LogOutput.log" 2>/dev/null; then say "B entered Lobby"; break; fi
done
say "--- A ---"; grep -E '\[scene\] -> |\[lobby\]' "$GAME/BepInEx/LogOutput.log" | tail -3
say "--- B ---"; grep -E '\[scene\] -> |\[join\]|\[ui\] click' "$B/BepInEx/LogOutput.log" | tail -4
