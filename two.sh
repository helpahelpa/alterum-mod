#!/bin/bash
# Launch exactly two Alterum instances, windowed and tiled, no automation.
#   A = the PINNED install (C:\AlterumPinned), B = C:\AlterumB (playerB)
set -e
# ALTERUM_GAME overrides A; see pinned/UPDATE-POLICY.md.
GAME="${ALTERUM_GAME:-/mnt/c/AlterumPinned}"
B="/mnt/c/AlterumB"
HERE="$(cd "$(dirname "$0")" && pwd)"

say() { echo "[two] $*"; }

# --- hard reset: nothing may survive from a previous run -------------------
cat > /tmp/twokill.txt <<'PS'
Get-Process -Name Alterum -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
(Get-Process -Name Alterum -ErrorAction SilentlyContinue | Measure-Object).Count
PS
left=$(/tmp/pwsh.py < /tmp/twokill.txt 2>/dev/null | tail -1)
say "after kill: $left running"

# --- config: no automation, windowed, low-res (LAUNCH-POLICY.md) ----------
python3 - "$GAME" "$B" <<'PY'
import re, sys
game, b = sys.argv[1], sys.argv[2]
specs = [(f"{game}/BepInEx/config/com.user0.alterum.modelswap.cfg", 0),
         (f"{b}/BepInEx/config/com.user0.alterum.modelswap.b.cfg", 960)]
for path, x in specs:
    s = open(path, encoding='utf-8-sig').read()
    def setk(k, v):
        global s
        if re.search(r'(?m)^'+k+r' = ', s):
            s = re.sub(r'(?m)^'+k+r' = .*$', k+' = '+v, s)
        else:
            s = s.replace('[Window]', '[Window]\n'+k+' = '+v, 1)
    setk('ClickSequence', '')          # no lobby, no clicking
    setk('ForceWindowed', 'true')
    setk('Quiet', 'false')
    setk('QuietWidth', '640')
    setk('QuietHeight', '360')
    setk('WindowX', str(x))
    setk('WindowY', '0')
    setk('Screenshots', 'false')
    setk('AutoPlace', 'false')         # nothing to place without a lobby
    open(path, 'w').write(s)

# BepInEx console back on, disk log untouched (section-scoped this time)
for cfg in (f"{game}/BepInEx/config/BepInEx.cfg", f"{b}/BepInEx/config/BepInEx.cfg"):
    lines = open(cfg, encoding='utf-8-sig').read().splitlines()
    cur, out, hit = None, [], False
    for ln in lines:
        if ln.startswith('[') and ln.rstrip().endswith(']'):
            cur = ln.strip()[1:-1]
        if cur == 'Logging.Console' and re.match(r'^Enabled\s*=', ln):
            ln = 'Enabled = true'; hit = True
        out.append(ln)
    open(cfg, 'w').write("\n".join(out) + "\n")
    print(f"{cfg.split('/')[-3]}/BepInEx.cfg console={hit}")
PY

# --- launch A then B -------------------------------------------------------
say "launching A"
/tmp/winrun.sh "$HERE/win/run-game.ps1" 2>/dev/null | tail -1
sleep 6
say "launching B"
ssh windows powershell -NoProfile -ExecutionPolicy Bypass -File "C:\\Windows\\Temp\\alterum\\run-second.ps1" \
  -GameDir "C:\\AlterumB" -GameArgs '"-screen-fullscreen 0 -screen-width 640 -screen-height 360 -alterumInstance=b"' 2>&1 \
  | grep -vE 'post-quantum|store now|openssh|vulnerable|WARNING: conn' | tail -1

sleep 20
cat > /tmp/twolist.txt <<'PS'
$p = @(Get-Process -Name Alterum -ErrorAction SilentlyContinue)
"count=" + $p.Count
$p | ForEach-Object { "  {0} {1}" -f $_.Id, (Get-Process -Id $_.Id).Path }
PS
say "result:"
/tmp/pwsh.py < /tmp/twolist.txt 2>/dev/null | tail -n +2
