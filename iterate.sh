#!/bin/bash
# Alterum mod iteration.
#   ./iterate.sh hot      -> rebuild ONLY the hot assembly and copy it; live in ~1s, no restart
#   ./iterate.sh quiet    -> full restart, off-screen window (unattended)
#   ./iterate.sh headed   -> full restart, visible + private lobby (standing rule: leave it up for the user)
set -e
# TARGET: the PINNED install. Frozen on Steam buildid 26597574; see pinned/UPDATE-POLICY.md.
# ALTERUM_GAME overrides, e.g. ALTERUM_GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/Alterum"
# to point at the live Steam copy (update testing only - never for day-to-day work).
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
HERE="$(cd "$(dirname "$0")" && pwd)"
# Only the merged tree may be deployed to A. The old shared `$HERE/mod` tree is the swap-era build
# (Contracts.SwapEnabled); its hot dll throws MissingFieldException every frame against the merged
# host, and it was copied into A's hot dir that way on 2026-11-12 15:21. See mod/STALE-DO-NOT-DEPLOY.md.
MOD="$HERE/tracks/merged/mod"
MODE="${1:-hot}"
INSTANCE="${2:-}"
HOTDIR="$GAME/BepInEx/hot${INSTANCE:+/$INSTANCE}"
CFG="$GAME/BepInEx/config/com.user0.alterum.modelswap.cfg"
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

build() { (cd "$MOD/$1" && dotnet build -c Release -v q --nologo | grep -E 'error CS' || true); }

# Atomic hot deploy: write a temp name and rename over the live file. A plain `cp` into the hot dir
# lets the host's watcher read a half-written assembly (BadImageFormatException, then a second
# reload); 21 MB takes long enough for that to happen every time.
deploy_hot() {
  local dir="$1"
  mkdir -p "$dir"
  cp "$MOD/AlterumModelSwap.Hot/bin/Release/AlterumModelSwap.Hot.dll" "$dir/.AlterumModelSwap.Hot.dll.tmp"
  mv -f "$dir/.AlterumModelSwap.Hot.dll.tmp" "$dir/AlterumModelSwap.Hot.dll"
}

if [ "$MODE" = "hot" ]; then
  build AlterumModelSwap.Hot
  mkdir -p "$HOTDIR"
  deploy_hot "$HOTDIR"
  echo "hot deployed to $HOTDIR; reloading in-game"
  sleep 3
  grep -E '\[hot\]|\[mirror\] created' "$GAME/BepInEx/LogOutput.log" | tail -4
  exit 0
fi

echo "[1/5] stopping game"; /tmp/winrun.sh /tmp/stop.ps1 >/dev/null
echo "[2/5] building"; build AlterumModelSwap.Contracts; build AlterumModelSwap; build AlterumModelSwap.Hot
echo "[3/5] deploying"
cp "$MOD/AlterumModelSwap/bin/Release/AlterumModelSwap.dll" "$GAME/BepInEx/plugins/"
cp "$MOD/AlterumModelSwap/bin/Release/AlterumModelSwap.Contracts.dll" "$GAME/BepInEx/plugins/"
mkdir -p "$GAME/BepInEx/hot"
deploy_hot "$GAME/BepInEx/hot"
echo "[4/5] configuring ($MODE)"
python3 - "$CFG" "$MODE" <<'PY'
import re, sys
p, mode = sys.argv[1], sys.argv[2]
s = open(p, encoding='utf-8-sig').read()
def setk(k, v):
    global s
    s = re.sub(r'(?m)^'+k+r' = .*$', k+' = '+v, s)
setk('Quiet', 'true' if mode == 'quiet' else 'false')
setk('Screenshots', 'true' if mode == 'quiet' else 'false')
setk('ClickSequence', 'Create Lobby Menu Button|@LobbyPrivate|Lobby Creator/Vertical Sectors/Buttons/Gothic')
setk('ClickFirstDelaySeconds', '7'); setk('ClickStepDelaySeconds', '3')
open(p, 'w').write(s)
PY
echo "[5/5] launching"
rm -f "$GAME/BepInEx/LogOutput.log"; rm -rf "$GAME/BepInEx/screens"
if [ "$MODE" = "headed" ]; then /tmp/winrun.sh "$HERE/win/run-game.ps1" | tail -2
else /tmp/winrun.sh "$HERE/win/run-quiet.ps1" | tail -2; fi
echo "waiting for lobby..."
for i in $(seq 1 40); do
  sleep 1
  if grep -q "\[scene\] -> 'Lobby'" "$GAME/BepInEx/LogOutput.log" 2>/dev/null; then
    echo "lobby up after ${i}s"; sleep 3
    grep -E '\[hot\] generation|\[mirror\] (using hardcoded|created)|error CS' "$GAME/BepInEx/LogOutput.log" | tail -5
    exit 0
  fi
done
echo "TIMEOUT"; tail -20 "$GAME/BepInEx/LogOutput.log"
